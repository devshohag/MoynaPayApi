using MoynaPay.Application.Abstractions;
using MoynaPay.Application.Orders;
using MoynaPay.Application.Workflows;
using MoynaPay.Domain.Orders;

namespace MoynaPay.Application.AppOrders;

public sealed class AppOrderActionService(
    AppOrderService appOrders,
    IOrderStore orders,
    OrderWorkflowService workflow,
    ReviewQueueService reviews)
{
    public async Task<AppOrderActionResult> DecideAsync(Guid merchantId, string reference,
        AppDecision decision, string actorName, string? reason, CancellationToken ct = default)
    {
        var order = await FindAsync(merchantId, reference, ct).ConfigureAwait(false);
        if (order is null) return new AppOrderActionResult(AppOrderActionOutcome.NotFound, null, null);

        var target = decision switch
        {
            AppDecision.Confirm => OrderStatus.Confirmed,
            AppDecision.Reject => OrderStatus.Rejected,
            _ => throw new ArgumentOutOfRangeException(nameof(decision), decision, "Unknown decision."),
        };

        var moved = await workflow.DecideAsync(new WorkflowDecisionCommand
        {
            MerchantId = merchantId,
            OrderId = order.Id,
            To = target,
            By = Actor.Merchant,
            ActorName = CleanActor(actorName),
            Reason = reason,
            EventType = target == OrderStatus.Confirmed ? "order.confirmed" : "order.rejected",
        }, ct).ConfigureAwait(false);

        return await FromWorkflowAsync(merchantId, moved, ct).ConfigureAwait(false);
    }

    public async Task<AppOrderActionResult> RecallAsync(Guid merchantId, string reference,
        string actorName, string? reason, CancellationToken ct = default)
    {
        var order = await FindAsync(merchantId, reference, ct).ConfigureAwait(false);
        if (order is null) return new AppOrderActionResult(AppOrderActionOutcome.NotFound, null, null);

        if (order.Status != OrderStatus.Rejected)
        {
            return new AppOrderActionResult(
                AppOrderActionOutcome.Refused, AppOrderDetail.From(order),
                "Only a rejected order can be recalled.");
        }

        var moved = await workflow.DecideAsync(new WorkflowDecisionCommand
        {
            MerchantId = merchantId,
            OrderId = order.Id,
            To = OrderStatus.Confirmed,
            By = Actor.Merchant,
            ActorName = CleanActor(actorName),
            Reason = reason ?? "merchant recalled rejected order",
            EventType = "order.recalled",
        }, ct).ConfigureAwait(false);

        return await FromWorkflowAsync(merchantId, moved, ct).ConfigureAwait(false);
    }

    public async Task<AppReviewClaimActionResult> ClaimReviewAsync(Guid merchantId,
        string reference, string reviewer, TimeSpan claimFor, CancellationToken ct = default)
    {
        var order = await FindAsync(merchantId, reference, ct).ConfigureAwait(false);
        if (order is null)
        {
            return new AppReviewClaimActionResult(
                ReviewClaimOutcome.NotFound, null, null);
        }

        var claimed = await reviews.ClaimAsync(new ReviewClaimCommand
        {
            MerchantId = merchantId,
            OrderId = order.Id,
            Reviewer = reviewer,
            ClaimFor = claimFor,
        }, ct).ConfigureAwait(false);

        return new AppReviewClaimActionResult(
            claimed.Outcome,
            claimed.Order is null ? null : AppOrderDetail.From(claimed.Order),
            claimed.Reason);
    }

    public async Task<AppOrderActionResult> MarkShippedAsync(Guid merchantId, string reference,
        string actorName, string? reason, CancellationToken ct = default)
    {
        var order = await FindAsync(merchantId, reference, ct).ConfigureAwait(false);
        if (order is null) return new AppOrderActionResult(AppOrderActionOutcome.NotFound, null, null);

        var moved = await workflow.DecideAsync(new WorkflowDecisionCommand
        {
            MerchantId = merchantId,
            OrderId = order.Id,
            To = OrderStatus.Shipped,
            By = Actor.Merchant,
            ActorName = CleanActor(actorName),
            Reason = reason,
            EventType = "order.shipped",
        }, ct).ConfigureAwait(false);

        return await FromWorkflowAsync(merchantId, moved, ct).ConfigureAwait(false);
    }

    private Task<Order?> FindAsync(Guid merchantId, string reference, CancellationToken ct) =>
        string.IsNullOrWhiteSpace(reference)
            ? Task.FromResult<Order?>(null)
            : orders.FindByReferenceAsync(merchantId, reference.Trim(), ct);

    private async Task<AppOrderActionResult> FromWorkflowAsync(Guid merchantId,
        WorkflowDecisionResult result, CancellationToken ct)
    {
        var detail = result.Order is null
            ? null
            : await appOrders.DetailAsync(merchantId, result.Order.Reference, ct).ConfigureAwait(false);

        return result.Outcome switch
        {
            TransitionOutcome.Moved => new AppOrderActionResult(AppOrderActionOutcome.Moved, detail, null),
            TransitionOutcome.Unchanged => new AppOrderActionResult(AppOrderActionOutcome.Unchanged, detail, null),
            TransitionOutcome.NotFound => new AppOrderActionResult(AppOrderActionOutcome.NotFound, null, null),
            _ => new AppOrderActionResult(AppOrderActionOutcome.Refused, detail, result.Reason),
        };
    }

    private static string CleanActor(string actorName) =>
        string.IsNullOrWhiteSpace(actorName) ? "app" : actorName.Trim();
}

public enum AppDecision
{
    Confirm,
    Reject,
}

public enum AppOrderActionOutcome
{
    Moved,
    Unchanged,
    Refused,
    NotFound,
}

public sealed record AppOrderActionResult(
    AppOrderActionOutcome Outcome,
    AppOrderDetail? Order,
    string? Reason);

public sealed record AppReviewClaimActionResult(
    ReviewClaimOutcome Outcome,
    AppOrderDetail? Order,
    string? Reason);
