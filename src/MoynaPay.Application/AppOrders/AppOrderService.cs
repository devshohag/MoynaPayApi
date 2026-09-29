using MoynaPay.Application.Abstractions;
using MoynaPay.Domain.Orders;

namespace MoynaPay.Application.AppOrders;

public sealed class AppOrderService(IOrderStore orders)
{
    public async Task<AppOrderPage> ListAsync(Guid merchantId, AppOrderListQuery query,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        var rows = await orders.ListAsync(merchantId, new OrderQuery
        {
            Status = query.Status,
            Search = query.Search,
            Take = query.Take,
            Before = query.Before,
            From = query.From,
            To = query.To,
        }, ct).ConfigureAwait(false);

        var items = rows.Select(AppOrderSummary.From).ToList();
        var nextBefore = items.Count == Math.Clamp(query.Take, 1, 200)
            ? items[^1].CreatedAt
            : (DateTimeOffset?)null;

        return new AppOrderPage(items, nextBefore);
    }

    public async Task<AppOrderDetail?> DetailAsync(Guid merchantId, string reference,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(reference)) return null;

        var order = await orders.FindByReferenceAsync(merchantId, reference.Trim(), ct)
            .ConfigureAwait(false);

        return order is null ? null : AppOrderDetail.From(order);
    }

    public async Task<IReadOnlyList<AppOrderTimelineItem>> TimelineAsync(
        Guid merchantId, string reference, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(reference)) return [];

        var order = await orders.FindByReferenceAsync(merchantId, reference.Trim(), ct)
            .ConfigureAwait(false);
        if (order is null) return [];

        var rows = await orders.TimelineAsync(merchantId, order.Id, ct).ConfigureAwait(false);

        return rows.Select(AppOrderTimelineItem.FromEvent).ToList();
    }
}

public sealed record AppOrderListQuery
{
    public OrderStatus? Status { get; init; }
    public string? Search { get; init; }
    public int Take { get; init; } = 50;
    public DateTimeOffset? Before { get; init; }
    public DateTimeOffset? From { get; init; }
    public DateTimeOffset? To { get; init; }
}

public sealed record AppOrderPage(
    IReadOnlyList<AppOrderSummary> Items,
    DateTimeOffset? NextBefore);

public sealed record AppOrderSummary(
    string Reference,
    OrderStatus Status,
    string CustomerName,
    string Msisdn,
    decimal Amount,
    decimal? ChargedAmount,
    string? Summary,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt)
{
    public static AppOrderSummary From(Order order) => new(
        order.Reference,
        order.Status,
        order.CustomerName,
        order.Msisdn,
        order.Amount,
        order.ChargedAmount,
        order.Summary,
        order.CreatedAt,
        order.UpdatedAt);
}

public sealed record AppOrderDetail(
    string Reference,
    OrderStatus Status,
    string CustomerName,
    string Msisdn,
    string? Address,
    string? Summary,
    decimal Amount,
    decimal? ChargedAmount,
    decimal? PaidAmount,
    string Currency,
    string? Reason,
    string? Digit,
    string? TrxId,
    string? Courier,
    string? TrackingCode,
    int Attempts,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? ConfirmedAt,
    DateTimeOffset? PaidAt,
    DateTimeOffset? ShippedAt,
    DateTimeOffset? ClosedAt,
    string? ClaimedBy,
    DateTimeOffset? ClaimedUntil)
{
    public static AppOrderDetail From(Order order) => new(
        order.Reference,
        order.Status,
        order.CustomerName,
        order.Msisdn,
        order.Address,
        order.Summary,
        order.Amount,
        order.ChargedAmount,
        order.PaidAmount,
        order.Currency,
        order.Reason,
        order.Digit,
        order.TrxId,
        order.Courier,
        order.TrackingCode,
        order.CallAttempts,
        order.CreatedAt,
        order.UpdatedAt,
        order.ConfirmedAt,
        order.PaidAt,
        order.ShippedAt,
        order.ClosedAt,
        order.ClaimedBy,
        order.ClaimedUntil);
}

public sealed record AppOrderTimelineItem(
    string Type,
    Actor Actor,
    string? ActorName,
    OrderStatus? From,
    OrderStatus? To,
    string? Detail,
    DateTimeOffset At)
{
    public static AppOrderTimelineItem FromEvent(OrderEvent row) => new(
        row.Type,
        row.Actor,
        row.ActorName,
        row.From,
        row.To,
        row.Detail,
        row.At);
}
