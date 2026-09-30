using MoynaPay.Application.Abstractions;
using MoynaPay.Application.Orders;
using MoynaPay.Application.Workflows;
using MoynaPay.Domain.Orders;

namespace MoynaPay.Application.Voice;

public sealed record CallOutcomeCommand
{
    public required Guid MerchantId { get; init; }
    public required Guid OrderId { get; init; }
    public required Guid CallSessionId { get; init; }
    public CallOutcome? Outcome { get; init; }
}

public enum CallOutcomeApplyKind
{
    Moved = 0,
    Retried = 1,
    Unchanged = 2,
    Refused = 3,
    NotFound = 4,
}

public sealed record CallOutcomeApplyResult(
    CallOutcomeApplyKind Kind,
    CallOutcome Outcome,
    Order? Order,
    DateTimeOffset? NextAttemptAt,
    string? Reason);

/// <summary>
/// Writes down what happened on a call, then asks the order workflow to apply it.
/// The call event is idempotent per call session; the order move still goes through the
/// ordinary workflow door, so webhooks and lifecycle rules stay in one place.
/// </summary>
public sealed class CallOutcomeService(
    IOrderStore orders,
    OrderWorkflowService workflow,
    IClock clock)
{
    public async Task<CallOutcomeApplyResult> ApplyAsync(
        CallOutcomeCommand command, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        var final = command.Outcome ?? CallOutcome.NoAnswer;
        var now = clock.UtcNow;
        var digit = Digit(final);
        var reason = Reason(final);

        await orders.SaveCallOutcomeAsync(
            command.MerchantId,
            command.OrderId,
            command.CallSessionId,
            final,
            digit,
            reason,
            now,
            ct).ConfigureAwait(false);

        if (final is CallOutcome.NoAnswer or CallOutcome.Failed or CallOutcome.Unreachable
            && await ScheduleRetryAsync(command, final, now, reason, ct).ConfigureAwait(false)
                is { } retry)
        {
            return retry;
        }

        var target = OrderLifecycle.FromCallOutcome(final);
        var result = await workflow.DecideAsync(new WorkflowDecisionCommand
        {
            MerchantId = command.MerchantId,
            OrderId = command.OrderId,
            To = target,
            By = Actor.Machine,
            Reason = reason,
            Digit = digit,
            EventType = EventType(target),
        }, ct).ConfigureAwait(false);

        return new CallOutcomeApplyResult(
            Map(result.Outcome),
            final,
            result.Order,
            null,
            result.Reason);
    }

    private async Task<CallOutcomeApplyResult?> ScheduleRetryAsync(
        CallOutcomeCommand command,
        CallOutcome outcome,
        DateTimeOffset now,
        string reason,
        CancellationToken ct)
    {
        var order = await orders
            .FindByIdAsync(command.MerchantId, command.OrderId, ct).ConfigureAwait(false);

        if (order is null)
        {
            return new CallOutcomeApplyResult(
                CallOutcomeApplyKind.NotFound, outcome, null, null, null);
        }

        var next = new RedialPolicy().NextAttemptAt(
            order.CallAttempts,
            order.LastCallAttemptAt,
            new CallingHours(),
            now);

        if (next is null) return null;

        await orders.ScheduleNextCallAsync(
            command.MerchantId,
            command.OrderId,
            next.Value,
            now,
            reason,
            ct).ConfigureAwait(false);

        var updated = await orders
            .FindByIdAsync(command.MerchantId, command.OrderId, ct).ConfigureAwait(false);

        return new CallOutcomeApplyResult(
            CallOutcomeApplyKind.Retried,
            outcome,
            updated,
            next,
            null);
    }

    private static string? Digit(CallOutcome outcome) => outcome switch
    {
        CallOutcome.Confirmed => "1",
        CallOutcome.Rejected => "0",
        CallOutcome.NeedsHuman => "9",
        _ => null,
    };

    private static string Reason(CallOutcome outcome) => outcome switch
    {
        CallOutcome.NoAnswer => "call ended with no keypress",
        CallOutcome.Failed => "call failed",
        CallOutcome.Unreachable => "call unreachable",
        _ => "call ended with keypress",
    };

    private static string EventType(OrderStatus target) => target switch
    {
        OrderStatus.Confirmed => "order.confirmed",
        OrderStatus.Rejected => "order.rejected",
        OrderStatus.NeedsHuman => "order.needs_human",
        _ => "order.changed",
    };

    private static CallOutcomeApplyKind Map(TransitionOutcome outcome) => outcome switch
    {
        TransitionOutcome.Moved => CallOutcomeApplyKind.Moved,
        TransitionOutcome.Unchanged => CallOutcomeApplyKind.Unchanged,
        TransitionOutcome.Refused => CallOutcomeApplyKind.Refused,
        TransitionOutcome.NotFound => CallOutcomeApplyKind.NotFound,
        _ => CallOutcomeApplyKind.Refused,
    };
}
