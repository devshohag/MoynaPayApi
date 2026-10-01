using MoynaPay.Application.Abstractions;
using MoynaPay.Application.Orders;
using MoynaPay.Application.Payments.Invoicing;
using MoynaPay.Application.Payments.Parsing;
using MoynaPay.Domain.Orders;
using MoynaPay.Domain.Payments;

namespace MoynaPay.Application.Payments.Matching;

public sealed class PaymentPipeline(
    IRawEventStore rawEvents,
    IInvoiceStore invoices,
    IOrderStore orders,
    OrderTransitionService transitions,
    IMessageParser parser,
    IClock clock)
{
    public async Task<PaymentPipelineRunResult> RunOnceAsync(
        int batchSize = 50, CancellationToken ct = default)
    {
        var batch = await rawEvents.ClaimReceivedAsync(batchSize, ct).ConfigureAwait(false);
        var items = new List<PaymentPipelineItemResult>(batch.Count);

        foreach (var rawEvent in batch)
        {
            items.Add(await ProcessAsync(rawEvent, ct).ConfigureAwait(false));
        }

        return new PaymentPipelineRunResult(items);
    }

    private async Task<PaymentPipelineItemResult> ProcessAsync(RawEvent rawEvent, CancellationToken ct)
    {
        var parsed = parser.Parse(rawEvent.SenderId, rawEvent.Body);

        if (parsed.Kind == MessageKind.Unknown)
        {
            rawEvent.State = RawEventState.Unparseable;
            rawEvent.FailureReason = parsed.FailureReason;
            rawEvent.UpdatedAt = clock.UtcNow;
            await rawEvents.SaveAsync(rawEvent, ct).ConfigureAwait(false);
            return Result(rawEvent, parsed, MatchOutcome.NeedsReview, parsed.FailureReason);
        }

        if (!parsed.Kind.IsCredit())
        {
            rawEvent.State = RawEventState.Ignored;
            rawEvent.UpdatedAt = clock.UtcNow;
            await rawEvents.SaveAsync(rawEvent, ct).ConfigureAwait(false);
            return Result(rawEvent, parsed, null, null);
        }

        if (parsed is not { Amount: { } amount, TrxId: { } trxId, OccurredAt: { } occurredAt })
        {
            rawEvent.State = RawEventState.Unparseable;
            rawEvent.FailureReason = "Credit message was missing amount, transaction id or timestamp.";
            rawEvent.UpdatedAt = clock.UtcNow;
            await rawEvents.SaveAsync(rawEvent, ct).ConfigureAwait(false);
            return Result(rawEvent, parsed, MatchOutcome.NeedsReview, rawEvent.FailureReason);
        }

        rawEvent.State = RawEventState.Parsed;
        rawEvent.UpdatedAt = clock.UtcNow;
        await rawEvents.SaveAsync(rawEvent, ct).ConfigureAwait(false);

        if (await invoices.HasMatchedTransactionAsync(PaymentMethod.Bkash, trxId, ct)
                .ConfigureAwait(false))
        {
            return Result(rawEvent, parsed, MatchOutcome.AlreadyProcessed, null);
        }

        var candidates = await invoices
            .FindOpenByChargedAmountAsync(rawEvent.MerchantId, amount, occurredAt, ct)
            .ConfigureAwait(false);

        if (candidates.Count == 0)
        {
            await MarkForReviewAsync(rawEvent, "No open invoice expects this amount.", ct)
                .ConfigureAwait(false);
            return Result(rawEvent, parsed, MatchOutcome.Unmatched, "No open invoice expects this amount.");
        }

        if (candidates.Count > 1)
        {
            var reason = $"{candidates.Count} open invoices expect {amount}.";
            await MarkForReviewAsync(rawEvent, reason, ct).ConfigureAwait(false);
            return Result(rawEvent, parsed, MatchOutcome.NeedsReview, reason);
        }

        var invoice = candidates[0];
        var outcome = amount >= invoice.ChargedAmount
            ? MatchOutcome.Matched
            : MatchOutcome.Partial;
        invoice.Status = outcome == MatchOutcome.Matched
            ? InvoiceStatus.Paid
            : InvoiceStatus.Partial;
        invoice.PaidAt = occurredAt;
        invoice.UpdatedAt = clock.UtcNow;

        var transaction = new ParsedTransaction
        {
            Id = Guid.CreateVersion7(),
            TenantId = rawEvent.MerchantId,
            RawEventId = rawEvent.Id,
            Method = PaymentMethod.Bkash,
            Amount = amount,
            TrxId = trxId,
            SenderMsisdn = parsed.CounterpartyMsisdn,
            BalanceAfter = parsed.BalanceAfter,
            OccurredAt = occurredAt,
            Confidence = parsed.Confidence,
            CreatedAt = clock.UtcNow,
            UpdatedAt = clock.UtcNow,
        };

        await invoices.SaveMatchAsync(invoice, transaction, MatchStrategy.UniqueAmount, clock.UtcNow, ct)
            .ConfigureAwait(false);

        if (outcome == MatchOutcome.Matched)
        {
            await MarkOrderPaidAsync(invoice, amount, trxId, ct).ConfigureAwait(false);
        }

        return Result(rawEvent, parsed, outcome, null, invoice.Id, MatchStrategy.UniqueAmount);
    }

    private async Task MarkForReviewAsync(RawEvent rawEvent, string reason, CancellationToken ct)
    {
        rawEvent.FailureReason = reason;
        rawEvent.UpdatedAt = clock.UtcNow;
        await rawEvents.SaveAsync(rawEvent, ct).ConfigureAwait(false);
    }

    private async Task MarkOrderPaidAsync(
        Invoice invoice, decimal paidAmount, string trxId, CancellationToken ct)
    {
        var order = await orders
            .FindByReferenceAsync(invoice.TenantId, invoice.OrderRef, ct)
            .ConfigureAwait(false);
        if (order is null) return;

        order.PaidAmount = paidAmount;
        order.TrxId = trxId;
        order.ChargedAmount ??= invoice.ChargedAmount;

        await transitions.ApplyAsync(new TransitionCommand
        {
            MerchantId = invoice.TenantId,
            OrderId = order.Id,
            To = OrderStatus.Paid,
            By = Actor.Machine,
            EventType = "order.paid",
            Reason = "payment matched",
        }, ct).ConfigureAwait(false);
    }

    private static PaymentPipelineItemResult Result(
        RawEvent rawEvent,
        ParsedMessage parsed,
        MatchOutcome? match,
        string? reason,
        Guid? invoiceId = null,
        MatchStrategy? strategy = null) =>
        new(
            rawEvent.Id,
            rawEvent.State,
            parsed.Kind,
            parsed.TrxId,
            parsed.Amount,
            match,
            invoiceId,
            strategy,
            reason);
}

public enum MatchOutcome
{
    Matched = 0,
    Partial = 1,
    AlreadyProcessed = 2,
    Unmatched = 3,
    NeedsReview = 4,
}

public sealed record PaymentPipelineItemResult(
    Guid RawEventId,
    RawEventState State,
    MessageKind Kind,
    string? TrxId,
    decimal? Amount,
    MatchOutcome? Match,
    Guid? InvoiceId,
    MatchStrategy? Strategy,
    string? Reason);

public sealed record PaymentPipelineRunResult(IReadOnlyList<PaymentPipelineItemResult> Items)
{
    public int Processed => Items.Count;

    public int Settled => Items.Count(i => i.Match is MatchOutcome.Matched or MatchOutcome.Partial);

    public int NeedsAttention => Items.Count(i =>
        i.State == RawEventState.Unparseable
        || i.Match is MatchOutcome.NeedsReview or MatchOutcome.Unmatched);
}
