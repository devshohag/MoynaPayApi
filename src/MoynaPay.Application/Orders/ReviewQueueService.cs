using MoynaPay.Application.Abstractions;
using MoynaPay.Application.Workflows;
using MoynaPay.Domain.Orders;

namespace MoynaPay.Application.Orders;

public sealed class ReviewQueueService(
    IOrderStore orders,
    OrderWorkflowService workflow,
    IClock clock)
{
    public static readonly TimeSpan DefaultClaimFor = TimeSpan.FromMinutes(5);

    public async Task<IReadOnlyList<Order>> ListAvailableAsync(Guid merchantId,
        int take = 50, CancellationToken ct = default)
    {
        var now = clock.UtcNow;
        var rows = await orders.ListAsync(merchantId, new OrderQuery
        {
            NeedsHumanOnly = true,
            Take = take,
        }, ct).ConfigureAwait(false);

        return rows.Where(o => !o.IsClaimed(now)).ToList();
    }

    public Task<ReviewClaimResult> ClaimAsync(ReviewClaimCommand command,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        var reviewer = CleanReviewer(command.Reviewer);
        if (reviewer is null)
        {
            return Task.FromResult(new ReviewClaimResult(
                ReviewClaimOutcome.Invalid, null, "reviewer is required"));
        }

        var claimFor = command.ClaimFor <= TimeSpan.Zero ? DefaultClaimFor : command.ClaimFor;
        return ClaimCoreAsync(command.MerchantId, command.OrderId, reviewer, claimFor, ct);
    }

    private async Task<ReviewClaimResult> ClaimCoreAsync(Guid merchantId, Guid orderId,
        string reviewer, TimeSpan claimFor, CancellationToken ct)
    {
        var result = await orders.TryClaimReviewAsync(
            merchantId, orderId, reviewer, clock.UtcNow, claimFor, ct).ConfigureAwait(false);

        return result.Outcome switch
        {
            ReviewClaimStoreOutcome.Claimed => new ReviewClaimResult(
                ReviewClaimOutcome.Claimed, result.Order, null),
            ReviewClaimStoreOutcome.NotFound => new ReviewClaimResult(
                ReviewClaimOutcome.NotFound, null, null),
            ReviewClaimStoreOutcome.NotInReview => new ReviewClaimResult(
                ReviewClaimOutcome.NotInReview, result.Order, result.Reason),
            _ => new ReviewClaimResult(ReviewClaimOutcome.AlreadyClaimed, result.Order, result.Reason),
        };
    }

    public Task<ReviewReleaseResult> ReleaseAsync(ReviewReleaseCommand command,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        var reviewer = CleanReviewer(command.Reviewer);
        if (reviewer is null)
        {
            return Task.FromResult(new ReviewReleaseResult(
                ReviewReleaseOutcome.Invalid, null, "reviewer is required"));
        }

        return ReleaseCoreAsync(command.MerchantId, command.OrderId, reviewer, ct);
    }

    private async Task<ReviewReleaseResult> ReleaseCoreAsync(Guid merchantId, Guid orderId,
        string reviewer, CancellationToken ct)
    {
        var result = await orders.ReleaseReviewClaimAsync(
            merchantId, orderId, reviewer, clock.UtcNow, ct).ConfigureAwait(false);

        return result.Outcome switch
        {
            ReviewReleaseStoreOutcome.Released => new ReviewReleaseResult(
                ReviewReleaseOutcome.Released, result.Order, null),
            ReviewReleaseStoreOutcome.NotFound => new ReviewReleaseResult(
                ReviewReleaseOutcome.NotFound, null, null),
            ReviewReleaseStoreOutcome.ClaimedByAnother => new ReviewReleaseResult(
                ReviewReleaseOutcome.ClaimedByAnother, result.Order, result.Reason),
            _ => new ReviewReleaseResult(ReviewReleaseOutcome.NotClaimed, result.Order, result.Reason),
        };
    }

    public async Task<ReviewDecisionResult> DecideAsync(ReviewDecisionCommand command,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        var reviewer = CleanReviewer(command.Reviewer);
        if (reviewer is null)
        {
            return new ReviewDecisionResult(
                ReviewDecisionOutcome.Invalid, null, "reviewer is required");
        }

        var order = await orders.FindByIdAsync(command.MerchantId, command.OrderId, ct)
            .ConfigureAwait(false);
        if (order is null) return new ReviewDecisionResult(ReviewDecisionOutcome.NotFound, null, null);

        if (order.Status != OrderStatus.NeedsHuman)
        {
            return new ReviewDecisionResult(
                ReviewDecisionOutcome.NotInReview, order, "The order is not waiting for review.");
        }

        var now = clock.UtcNow;
        if (!order.IsClaimed(now))
        {
            order.ClaimedBy = null;
            order.ClaimedUntil = null;
            await orders.SaveAuditAsync(order, new OrderEvent
            {
                TenantId = command.MerchantId,
                OrderId = order.Id,
                Type = "review.claim_expired",
                Actor = Actor.Merchant,
                ActorName = reviewer,
                From = order.Status,
                To = order.Status,
                At = now,
            }, ct).ConfigureAwait(false);

            return new ReviewDecisionResult(
                ReviewDecisionOutcome.NotClaimed, order, "The review claim has expired.");
        }

        if (!string.Equals(order.ClaimedBy, reviewer, StringComparison.Ordinal))
        {
            return new ReviewDecisionResult(
                ReviewDecisionOutcome.ClaimedByAnother, order, "The order is claimed by someone else.");
        }

        var (to, eventType) = command.Outcome switch
        {
            ReviewDecision.Confirm => (OrderStatus.Confirmed, "order.confirmed"),
            ReviewDecision.Reject => (OrderStatus.Rejected, "order.rejected"),
            ReviewDecision.CallAgain => (OrderStatus.Calling, "order.call_again"),
            _ => throw new ArgumentOutOfRangeException(nameof(command), "Unknown review outcome."),
        };

        var moved = await workflow.DecideAsync(new WorkflowDecisionCommand
        {
            MerchantId = command.MerchantId,
            OrderId = command.OrderId,
            To = to,
            By = Actor.Merchant,
            ActorName = reviewer,
            Reason = command.Reason,
            EventType = eventType,
        }, ct).ConfigureAwait(false);

        return moved.Outcome switch
        {
            TransitionOutcome.Moved => new ReviewDecisionResult(
                ReviewDecisionOutcome.Moved, moved.Order, null),
            TransitionOutcome.Unchanged => new ReviewDecisionResult(
                ReviewDecisionOutcome.Unchanged, moved.Order, null),
            TransitionOutcome.NotFound => new ReviewDecisionResult(
                ReviewDecisionOutcome.NotFound, null, null),
            _ => new ReviewDecisionResult(
                ReviewDecisionOutcome.Refused, moved.Order, moved.Reason),
        };
    }

    private static string? CleanReviewer(string? reviewer)
    {
        var trimmed = reviewer?.Trim();
        return string.IsNullOrWhiteSpace(trimmed) ? null : trimmed;
    }
}

public sealed record ReviewClaimCommand
{
    public required Guid MerchantId { get; init; }
    public required Guid OrderId { get; init; }
    public required string? Reviewer { get; init; }
    public TimeSpan ClaimFor { get; init; } = ReviewQueueService.DefaultClaimFor;
}

public enum ReviewClaimOutcome
{
    Claimed,
    AlreadyClaimed,
    NotInReview,
    NotFound,
    Invalid,
}

public sealed record ReviewClaimResult(ReviewClaimOutcome Outcome, Order? Order, string? Reason);

public sealed record ReviewReleaseCommand
{
    public required Guid MerchantId { get; init; }
    public required Guid OrderId { get; init; }
    public required string? Reviewer { get; init; }
}

public enum ReviewReleaseOutcome
{
    Released,
    NotClaimed,
    ClaimedByAnother,
    NotFound,
    Invalid,
}

public sealed record ReviewReleaseResult(ReviewReleaseOutcome Outcome, Order? Order, string? Reason);

public sealed record ReviewDecisionCommand
{
    public required Guid MerchantId { get; init; }
    public required Guid OrderId { get; init; }
    public required string? Reviewer { get; init; }
    public required ReviewDecision Outcome { get; init; }
    public string? Reason { get; init; }
}

public enum ReviewDecision
{
    Confirm,
    Reject,
    CallAgain,
}

public enum ReviewDecisionOutcome
{
    Moved,
    Unchanged,
    Refused,
    NotClaimed,
    ClaimedByAnother,
    NotInReview,
    NotFound,
    Invalid,
}

public sealed record ReviewDecisionResult(ReviewDecisionOutcome Outcome, Order? Order, string? Reason);
