using System.Text.Json;
using MoynaPay.Application.Abstractions;
using MoynaPay.Domain.Orders;

namespace MoynaPay.Application.Voice.Ai;

public sealed record HandoffContextCommand
{
    public required Guid MerchantId { get; init; }
    public required Guid OrderId { get; init; }
    public required Guid CallSessionId { get; init; }
    public required string TranscriptText { get; init; }
    public required string HandoffReason { get; init; }
}

public enum HandoffContextOutcome
{
    Recorded,
    NotFound,
}

public sealed record HandoffContextRecordResult(
    HandoffContextOutcome Outcome,
    Order? Order,
    HandoffContextResult? Context);

public sealed class HandoffContextService(
    IOrderStore orders,
    IHandoffContextSummarizer summarizer,
    IClock clock)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<HandoffContextRecordResult> RecordAsync(
        HandoffContextCommand command,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        var order = await orders.FindByIdAsync(command.MerchantId, command.OrderId, ct)
            .ConfigureAwait(false);
        if (order is null)
            return new HandoffContextRecordResult(HandoffContextOutcome.NotFound, null, null);

        var context = await summarizer.SummarizeAsync(
            command.MerchantId,
            command.TranscriptText,
            command.HandoffReason,
            ct).ConfigureAwait(false);

        var now = clock.UtcNow;
        await orders.SaveAuditAsync(order, new OrderEvent
        {
            TenantId = command.MerchantId,
            OrderId = command.OrderId,
            Type = "voice.handoff_context",
            Actor = Actor.Machine,
            From = order.Status,
            To = order.Status,
            Detail = context.Summary,
            PayloadJson = JsonSerializer.Serialize(new
            {
                callSessionId = command.CallSessionId,
                handoffReason = command.HandoffReason,
                context,
            }, Json),
            At = now,
            CreatedAt = now,
            UpdatedAt = now,
        }, ct).ConfigureAwait(false);

        return new HandoffContextRecordResult(
            HandoffContextOutcome.Recorded,
            order,
            context);
    }
}
