using MoynaPay.Application.Abstractions;
using MoynaPay.Domain.Merchants;
using MoynaPay.Domain.Orders;

namespace MoynaPay.Application.AppBootstrap;

public sealed class AppBootstrapService(
    IMerchantStore merchants,
    IOrderStore orders)
{
    public async Task<AppBootstrapResult> GetAsync(Guid merchantId, CancellationToken ct = default)
    {
        var merchant = await merchants.FindAsync(merchantId, ct).ConfigureAwait(false);
        if (merchant is null) return new AppBootstrapResult(null);

        var subscription = await merchants.SubscriptionAsync(merchantId, ct).ConfigureAwait(false);
        var webhook = await merchants.WebhookAsync(merchantId, ct).ConfigureAwait(false);
        var keys = await merchants.ListCredentialsAsync(merchantId, ct).ConfigureAwait(false);
        var counts = await orders.CountByStatusAsync(merchantId, ct).ConfigureAwait(false);

        return new AppBootstrapResult(new AppBootstrap
        {
            Merchant = new MerchantSummary(
                merchant.Id,
                merchant.Name,
                merchant.Msisdn,
                merchant.Status,
                merchant.TimeZone,
                merchant.Address,
                merchant.SupportMsisdn),
            EnabledServices = new EnabledServices(
                subscription.Calls,
                subscription.Payments,
                subscription.Courier,
                subscription.Plan),
            Settings = new SettingsSummary(
                WebhookConfigured: webhook is not null,
                WebhookActive: webhook?.Active ?? false,
                ActiveApiKeys: keys.Count(k => k.IsActive),
                TimeZone: merchant.TimeZone),
            Counters = AppCounters.From(counts),
        });
    }
}

public sealed record AppBootstrapResult(AppBootstrap? Bootstrap)
{
    public bool Found => Bootstrap is not null;
}

public sealed class AppBootstrap
{
    public required MerchantSummary Merchant { get; init; }
    public required EnabledServices EnabledServices { get; init; }
    public required SettingsSummary Settings { get; init; }
    public required AppCounters Counters { get; init; }
}

public sealed record MerchantSummary(
    Guid Id,
    string Name,
    string Msisdn,
    MerchantStatus Status,
    string TimeZone,
    string? Address,
    string? SupportMsisdn);

public sealed record EnabledServices(bool Calls, bool Payments, bool Courier, string Plan);

public sealed record SettingsSummary(
    bool WebhookConfigured,
    bool WebhookActive,
    int ActiveApiKeys,
    string TimeZone);

public sealed record AppCounters(
    int New,
    int Calling,
    int NeedsHuman,
    int AwaitingPayment,
    int Paid,
    int Booked,
    int Shipped,
    int Open)
{
    public static AppCounters From(IReadOnlyDictionary<OrderStatus, int> counts)
    {
        var open = counts
            .Where(pair => OrderLifecycle.IsOpen(pair.Key))
            .Sum(pair => pair.Value);

        return new AppCounters(
            Get(OrderStatus.Received),
            Get(OrderStatus.Calling),
            Get(OrderStatus.NeedsHuman),
            Get(OrderStatus.AwaitingPayment),
            Get(OrderStatus.Paid),
            Get(OrderStatus.Booked),
            Get(OrderStatus.Shipped),
            open);

        int Get(OrderStatus status) => counts.TryGetValue(status, out var count) ? count : 0;
    }
}
