using MoynaPay.Application.Abstractions;
using MoynaPay.Application.Orders;
using MoynaPay.Domain.Orders;

namespace MoynaPay.Application.Workflows;

public sealed class OrderWorkflowService(
    CreateOrderService createOrders,
    OrderTransitionService transitions,
    IOrderStore orders,
    IMerchantStore merchants,
    IWorkflowSessionStore sessions,
    IClock clock)
{
    public const string WorkflowName = "OrderConfirmation";

    public async Task<CreateOrderResult> CreateAsync(CreateOrderCommand command,
        CancellationToken ct = default)
    {
        var result = await createOrders.CreateAsync(command, ct).ConfigureAwait(false);
        if (!result.Succeeded) return result;

        if (result.Outcome == CreateOrderOutcome.AlreadyExists
            && await sessions.FindAsync(command.MerchantId, result.Order!.Id, WorkflowName, ct)
                .ConfigureAwait(false) is not null)
        {
            return result;
        }

        await RecordAsync(command.MerchantId, result.Order!, "order.received", ct).ConfigureAwait(false);

        return result;
    }

    public async Task<WorkflowDecisionResult> DecideAsync(WorkflowDecisionCommand command,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        var order = await orders.FindByIdAsync(command.MerchantId, command.OrderId, ct)
            .ConfigureAwait(false);
        if (order is null) return new WorkflowDecisionResult(TransitionOutcome.NotFound, null, null);

        var first = await transitions.ApplyAsync(new TransitionCommand
        {
            MerchantId = command.MerchantId,
            OrderId = command.OrderId,
            To = command.To,
            By = command.By,
            ActorName = command.ActorName,
            Reason = command.Reason,
            Digit = command.Digit,
            EventType = command.EventType,
        }, ct).ConfigureAwait(false);

        if (first.Outcome is not (TransitionOutcome.Moved or TransitionOutcome.Unchanged))
        {
            return new WorkflowDecisionResult(first.Outcome, first.Order, first.Reason);
        }

        order = first.Order!;
        await RecordAsync(command.MerchantId, order, command.EventType, ct).ConfigureAwait(false);

        if (first.Outcome == TransitionOutcome.Moved && command.To == OrderStatus.Confirmed)
        {
            var subscription = await merchants.SubscriptionAsync(command.MerchantId, ct).ConfigureAwait(false);
            if (OrderLifecycle.AfterConfirmed(subscription) is { } next)
            {
                var follow = await transitions.ApplyAsync(new TransitionCommand
                {
                    MerchantId = command.MerchantId,
                    OrderId = command.OrderId,
                    To = next,
                    By = Actor.Machine,
                    Reason = "workflow advanced after confirmation",
                    EventType = $"workflow.{next.ToString().ToLowerInvariant()}",
                }, ct).ConfigureAwait(false);

                if (follow.Outcome is TransitionOutcome.Moved or TransitionOutcome.Unchanged)
                {
                    order = follow.Order!;
                    await RecordAsync(command.MerchantId, order, $"workflow.{next}", ct).ConfigureAwait(false);
                }
            }
        }

        return new WorkflowDecisionResult(TransitionOutcome.Moved, order, null);
    }

    private async Task RecordAsync(Guid merchantId, Order order, string eventName,
        CancellationToken ct)
    {
        var session = await sessions.FindAsync(merchantId, order.Id, WorkflowName, ct).ConfigureAwait(false)
            ?? new WorkflowSession
            {
                MerchantId = merchantId,
                OrderId = order.Id,
                Name = WorkflowName,
            };

        session.Step = order.Status.ToString();
        session.Complete = OrderLifecycle.IsClosed(order.Status)
            || order.Status is OrderStatus.Confirmed or OrderStatus.NeedsHuman or OrderStatus.AwaitingPayment
                or OrderStatus.Booked or OrderStatus.Paid;
        session.UpdatedAt = clock.UtcNow;
        session.History.Add(eventName);

        await sessions.SaveAsync(session, ct).ConfigureAwait(false);
    }
}

public sealed record WorkflowDecisionCommand
{
    public required Guid MerchantId { get; init; }
    public required Guid OrderId { get; init; }
    public required OrderStatus To { get; init; }
    public required Actor By { get; init; }

    public string? ActorName { get; init; }
    public string? Reason { get; init; }
    public string? Digit { get; init; }
    public string EventType { get; init; } = "order.changed";
}

public sealed record WorkflowDecisionResult(
    TransitionOutcome Outcome,
    Order? Order,
    string? Reason);
