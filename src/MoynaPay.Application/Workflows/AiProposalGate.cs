using MoynaPay.Application.Abstractions;
using MoynaPay.Domain.Orders;

namespace MoynaPay.Application.Workflows;

public sealed class AiProposalGate(
    IOrderStore orders,
    OrderWorkflowService workflow)
{
    public const double DefaultConfidenceThreshold = 0.75;

    public async Task<AiGateResult> ApplyAsync(AiProposal proposal, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(proposal);

        var order = await orders.FindByIdAsync(proposal.MerchantId, proposal.OrderId, ct)
            .ConfigureAwait(false);
        if (order is null) return new AiGateResult(AiGateOutcome.NotFound, null, null);

        var target = DecideTarget(order, proposal);
        var result = await workflow.DecideAsync(new WorkflowDecisionCommand
        {
            MerchantId = proposal.MerchantId,
            OrderId = proposal.OrderId,
            To = target.Status,
            By = Actor.Machine,
            Reason = target.Reason,
            EventType = target.EventType,
        }, ct).ConfigureAwait(false);

        return new AiGateResult(target.Outcome, result.Order, result.Reason ?? target.Reason);
    }

    private static GateTarget DecideTarget(Order order, AiProposal proposal)
    {
        if (proposal.Confidence < proposal.ConfidenceThreshold)
        {
            return Review("ai.low_confidence");
        }

        return proposal.Outcome switch
        {
            AiProposedOutcome.Confirm when OrderLifecycle.CanMove(order.Status, OrderStatus.Confirmed, Actor.Machine) =>
                new GateTarget(AiGateOutcome.Executed, OrderStatus.Confirmed, "ai.proposed_confirm", "order.confirmed"),

            AiProposedOutcome.Confirm => Review("ai.against_rule"),
            AiProposedOutcome.Reject => Review("ai.reject_requires_human"),
            AiProposedOutcome.NeedsHuman => Review("ai.requested_review"),
            AiProposedOutcome.Reschedule => Review("ai.reschedule_requires_human"),
            _ => Review("ai.unknown_proposal"),
        };
    }

    private static GateTarget Review(string reason) =>
        new(AiGateOutcome.SentToReview, OrderStatus.NeedsHuman, reason, "order.needs_human");
}

public enum AiProposedOutcome
{
    Confirm,
    Reject,
    NeedsHuman,
    Reschedule,
}

public sealed record AiProposal
{
    public required Guid MerchantId { get; init; }
    public required Guid OrderId { get; init; }
    public required AiProposedOutcome Outcome { get; init; }
    public required double Confidence { get; init; }
    public double ConfidenceThreshold { get; init; } = AiProposalGate.DefaultConfidenceThreshold;
}

public enum AiGateOutcome
{
    Executed,
    SentToReview,
    NotFound,
}

public sealed record AiGateResult(AiGateOutcome Outcome, Order? Order, string? Reason);

internal sealed record GateTarget(
    AiGateOutcome Outcome,
    OrderStatus Status,
    string Reason,
    string EventType);
