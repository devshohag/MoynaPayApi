using MoynaPay.Application.Abstractions;
using MoynaPay.Application.Orders;
using MoynaPay.Application.Payments.Parsing;
using MoynaPay.Domain.Orders;
using MoynaPay.Domain.Payments;

namespace MoynaPay.Application.Payments.Matching;

public sealed class PaymentReviewService(
    IRawEventStore rawEvents,
    IInvoiceStore invoices,
    IOrderStore orders,
    OrderTransitionService transitions,
    IMessageParser parser,
    IClock clock)
{
    public async Task<IReadOnlyList<PaymentReviewItem>> ListAsync(Guid merchantId,
        CancellationToken ct = default)
    {
        var rows = await rawEvents.ListByMerchantAsync(merchantId, ct).ConfigureAwait(false);

        return rows
            .Where(e => e.State == RawEventState.Unparseable
                || e.State == RawEventState.Parsed && !string.IsNullOrWhiteSpace(e.FailureReason))
            .OrderBy(e => e.DeviceReceivedAt)
            .Select(e => new PaymentReviewItem(
                e.Id,
                e.State,
                e.SenderId,
                e.Body,
                e.DeviceReceivedAt,
                e.FailureReason))
            .ToList();
    }

    public async Task<ManualPaymentMatchResult> ManualMatchAsync(
        ManualPaymentMatchCommand command, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        var reviewer = Clean(command.Reviewer, 120);
        if (reviewer is null)
        {
            return new ManualPaymentMatchResult(ManualPaymentMatchOutcome.Invalid, null,
                "reviewer is required");
        }

        var rawEvent = await rawEvents.FindAsync(command.MerchantId, command.RawEventId, ct)
            .ConfigureAwait(false);
        if (rawEvent is null)
        {
            return new ManualPaymentMatchResult(ManualPaymentMatchOutcome.NotFound, null, null);
        }

        var invoice = await invoices.FindByOrderRefAsync(command.MerchantId, command.OrderRef, ct)
            .ConfigureAwait(false);
        if (invoice is null)
        {
            return new ManualPaymentMatchResult(ManualPaymentMatchOutcome.InvoiceNotFound, null, null);
        }

        var parsed = parser.Parse(rawEvent.SenderId, rawEvent.Body);
        if (!parsed.Kind.IsCredit()
            || parsed is not { Amount: { } amount, TrxId: { } trxId, OccurredAt: { } occurredAt })
        {
            return new ManualPaymentMatchResult(ManualPaymentMatchOutcome.Invalid, null,
                "raw event is not a readable credit payment");
        }

        if (await invoices.HasMatchedTransactionAsync(PaymentMethod.Bkash, trxId, ct)
                .ConfigureAwait(false))
        {
            return new ManualPaymentMatchResult(ManualPaymentMatchOutcome.AlreadyMatched, invoice, null);
        }

        invoice.Status = amount >= invoice.ChargedAmount
            ? InvoiceStatus.Paid
            : InvoiceStatus.Partial;
        invoice.PaidAt = occurredAt;
        invoice.UpdatedAt = clock.UtcNow;

        var transaction = new ParsedTransaction
        {
            Id = Guid.CreateVersion7(),
            TenantId = command.MerchantId,
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

        await invoices.SaveMatchAsync(invoice, transaction, MatchStrategy.Manual, clock.UtcNow, ct)
            .ConfigureAwait(false);

        rawEvent.State = RawEventState.Parsed;
        rawEvent.FailureReason = null;
        rawEvent.UpdatedAt = clock.UtcNow;
        await rawEvents.SaveAsync(rawEvent, ct).ConfigureAwait(false);

        var order = await orders.FindByReferenceAsync(command.MerchantId, invoice.OrderRef, ct)
            .ConfigureAwait(false);
        if (order is not null && invoice.Status == InvoiceStatus.Paid)
        {
            order.PaidAmount = amount;
            order.TrxId = trxId;
            order.ChargedAmount ??= invoice.ChargedAmount;

            await transitions.ApplyAsync(new TransitionCommand
            {
                MerchantId = command.MerchantId,
                OrderId = order.Id,
                To = OrderStatus.Paid,
                By = Actor.Machine,
                EventType = "order.paid",
                Reason = "payment manually matched",
            }, ct).ConfigureAwait(false);

            await orders.SaveAuditAsync(order, new OrderEvent
            {
                TenantId = command.MerchantId,
                OrderId = order.Id,
                Type = "payment.manual_match",
                Actor = Actor.Merchant,
                ActorName = reviewer,
                From = order.Status,
                To = order.Status,
                Detail = command.Reason,
                PayloadJson = $$"""
                    {"rawEventId":"{{rawEvent.Id}}","invoiceId":"{{invoice.Id}}","trxId":"{{trxId}}","amount":{{amount}}}
                    """,
                At = clock.UtcNow,
            }, ct).ConfigureAwait(false);
        }

        return new ManualPaymentMatchResult(ManualPaymentMatchOutcome.Matched, invoice, null);
    }

    private static string? Clean(string? value, int max)
    {
        var trimmed = value?.Trim();
        if (string.IsNullOrWhiteSpace(trimmed)) return null;

        return trimmed.Length <= max ? trimmed : trimmed[..max];
    }
}

public sealed record PaymentReviewItem(
    Guid RawEventId,
    RawEventState State,
    string SenderId,
    string Body,
    DateTimeOffset DeviceReceivedAt,
    string? Reason);

public sealed record ManualPaymentMatchCommand(
    Guid MerchantId,
    Guid RawEventId,
    string OrderRef,
    string? Reviewer,
    string? Reason);

public enum ManualPaymentMatchOutcome
{
    Matched,
    AlreadyMatched,
    InvoiceNotFound,
    NotFound,
    Invalid,
}

public sealed record ManualPaymentMatchResult(
    ManualPaymentMatchOutcome Outcome,
    Invoice? Invoice,
    string? Reason);
