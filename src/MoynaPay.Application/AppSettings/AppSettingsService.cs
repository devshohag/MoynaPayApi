using MoynaPay.Application.Abstractions;
using MoynaPay.Application.Merchants;
using MoynaPay.Application.Orders;
using MoynaPay.Application.Voice;
using MoynaPay.Domain.Merchants;
using MoynaPay.Domain.Payments;

namespace MoynaPay.Application.AppSettings;

public sealed class AppSettingsService(
    IMerchantStore merchants,
    WebhookService webhooks,
    IClock clock)
{
    public async Task<AppSettingsResult> GetAsync(Guid merchantId, CancellationToken ct = default)
    {
        var merchant = await merchants.FindAsync(merchantId, ct).ConfigureAwait(false);
        if (merchant is null) return new AppSettingsResult(null);

        var subscription = await merchants.SubscriptionAsync(merchantId, ct).ConfigureAwait(false);
        var webhook = await merchants.WebhookAsync(merchantId, ct).ConfigureAwait(false);
        var keys = await merchants.ListCredentialsAsync(merchantId, ct).ConfigureAwait(false);

        return new AppSettingsResult(new AppSettings
        {
            Profile = ProfileSettings.From(merchant),
            Subscription = SubscriptionSettings.From(subscription),
            Webhook = webhook is null ? null : WebhookSettings.From(webhook),
            ApiKeys = keys.Select(ApiKeySettings.From).ToList(),
            Calls = subscription.Calls ? CallSettings.Default(merchant.TimeZone) : null,
            Payments = subscription.Payments ? PaymentSettings.Default : null,
            Courier = subscription.Courier ? CourierSettings.Default : null,
        });
    }

    public async Task<AppSettingsResult> UpdateProfileAsync(Guid merchantId,
        UpdateProfileSettings command, CancellationToken ct = default)
    {
        var merchant = await merchants.FindAsync(merchantId, ct).ConfigureAwait(false);
        if (merchant is null) return new AppSettingsResult(null);

        if (!string.IsNullOrWhiteSpace(command.Name)) merchant.Name = command.Name.Trim();
        if (!string.IsNullOrWhiteSpace(command.TimeZone)) merchant.TimeZone = command.TimeZone.Trim();
        merchant.Address = BlankToNull(command.Address) ?? merchant.Address;
        merchant.SupportMsisdn = string.IsNullOrWhiteSpace(command.SupportMsisdn)
            ? merchant.SupportMsisdn
            : Msisdn.Normalise(command.SupportMsisdn);
        merchant.UpdatedAt = clock.UtcNow;

        await merchants.SaveMerchantProfileAsync(merchant, ct).ConfigureAwait(false);

        return await GetAsync(merchantId, ct).ConfigureAwait(false);
    }

    public async Task<WebhookResult> UpdateWebhookAsync(Guid merchantId,
        string? url, bool active, CancellationToken ct = default) =>
        await webhooks.RegisterAsync(merchantId, url, active, ct).ConfigureAwait(false);

    private static string? BlankToNull(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

public sealed record AppSettingsResult(AppSettings? Settings)
{
    public bool Found => Settings is not null;
}

public sealed class AppSettings
{
    public required ProfileSettings Profile { get; init; }
    public required SubscriptionSettings Subscription { get; init; }
    public WebhookSettings? Webhook { get; init; }
    public required IReadOnlyList<ApiKeySettings> ApiKeys { get; init; }
    public CallSettings? Calls { get; init; }
    public PaymentSettings? Payments { get; init; }
    public CourierSettings? Courier { get; init; }
}

public sealed record ProfileSettings(
    string Name,
    string Msisdn,
    string TimeZone,
    string? Address,
    string? SupportMsisdn)
{
    public static ProfileSettings From(Merchant merchant) => new(
        merchant.Name,
        merchant.Msisdn,
        merchant.TimeZone,
        merchant.Address,
        merchant.SupportMsisdn);
}

public sealed record SubscriptionSettings(bool Calls, bool Payments, bool Courier, string Plan)
{
    public static SubscriptionSettings From(Subscription subscription) => new(
        subscription.Calls,
        subscription.Payments,
        subscription.Courier,
        subscription.Plan);
}

public sealed record WebhookSettings(
    string Url,
    bool Active,
    DateTimeOffset? LastDeliveredAt,
    string? LastFailureReason)
{
    public static WebhookSettings From(WebhookEndpoint endpoint) => new(
        endpoint.Url,
        endpoint.Active,
        endpoint.LastDeliveredAt,
        endpoint.LastFailureReason);
}

public sealed record ApiKeySettings(string KeyId, string Label, bool Active, DateTimeOffset CreatedAt)
{
    public static ApiKeySettings From(ApiCredential key) => new(
        key.KeyId,
        key.Label,
        key.IsActive,
        key.CreatedAt);
}

public sealed record CallSettings(
    string TimeZone,
    string Opens,
    string Closes,
    int MaxAttempts,
    IReadOnlyList<int> RetryGapMinutes,
    string Greeting,
    int AnswerTimeoutSeconds,
    int Repeats)
{
    public static CallSettings Default(string timeZone)
    {
        var hours = new CallingHours();
        var redial = new RedialPolicy();
        var script = CallScript.Default;

        return new CallSettings(
            timeZone,
            hours.Opens.ToString("HH:mm"),
            hours.Closes.ToString("HH:mm"),
            redial.MaxAttempts,
            redial.Gaps.Select(g => (int)g.TotalMinutes).ToList(),
            script.Greeting,
            (int)script.AnswerTimeout.TotalSeconds,
            script.Repeats);
    }
}

public sealed record PaymentSettings(IReadOnlyList<string> Wallets, MatchingMode MatchingMode)
{
    public static PaymentSettings Default { get; } = new([], MatchingMode.TrxId);
}

public sealed record CourierSettings(IReadOnlyList<string> Providers, bool AutoBook)
{
    public static CourierSettings Default { get; } = new([], false);
}

public sealed record UpdateProfileSettings(
    string? Name,
    string? TimeZone,
    string? Address,
    string? SupportMsisdn);
