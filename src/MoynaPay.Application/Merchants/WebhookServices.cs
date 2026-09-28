using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using MoynaPay.Application.Abstractions;
using MoynaPay.Application.Security;
using MoynaPay.Domain.Merchants;

namespace MoynaPay.Application.Merchants;

public sealed class WebhookService(
    IMerchantStore merchants, ISecretProtector secrets, IWebhookSender sender, IClock clock)
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
    };

    public async Task<WebhookResult> RegisterAsync(Guid merchantId, string? url, bool active = true,
        CancellationToken ct = default)
    {
        var merchant = await merchants.FindAsync(merchantId, ct).ConfigureAwait(false);
        if (merchant is null) return new WebhookResult(WebhookOutcome.NoMerchant, null, null);

        if (!WebhookUrl.TryNormalize(url, out var normalized, out var reason))
        {
            return new WebhookResult(WebhookOutcome.Invalid, null, reason);
        }

        var now = clock.UtcNow;
        var existing = await merchants.WebhookAsync(merchantId, ct).ConfigureAwait(false);
        var endpoint = existing ?? new WebhookEndpoint
        {
            Id = Guid.CreateVersion7(),
            TenantId = merchantId,
            CreatedAt = now,
        };

        endpoint.Url = normalized;
        endpoint.Active = active;
        endpoint.UpdatedAt = now;

        if (existing is null)
        {
            var secret = MerchantSecrets.NewSecret();
            endpoint.SecretCipher = secrets.Protect(secret, out var keyRingId);
            endpoint.KeyRingId = keyRingId;
        }

        await merchants.SaveWebhookAsync(endpoint, ct).ConfigureAwait(false);

        return new WebhookResult(existing is null ? WebhookOutcome.Created : WebhookOutcome.Updated,
            endpoint, null);
    }

    public async Task<WebhookResult> RotateSecretAsync(Guid merchantId, CancellationToken ct = default)
    {
        var endpoint = await merchants.WebhookAsync(merchantId, ct).ConfigureAwait(false);
        if (endpoint is null) return new WebhookResult(WebhookOutcome.NotFound, null, null);

        var secret = MerchantSecrets.NewSecret();
        endpoint.SecretCipher = secrets.Protect(secret, out var keyRingId);
        endpoint.KeyRingId = keyRingId;
        endpoint.UpdatedAt = clock.UtcNow;

        await merchants.SaveWebhookAsync(endpoint, ct).ConfigureAwait(false);

        return new WebhookResult(WebhookOutcome.Updated, endpoint, null);
    }

    public async Task<WebhookTestResult> SendTestAsync(Guid merchantId, CancellationToken ct = default)
    {
        var endpoint = await merchants.WebhookAsync(merchantId, ct).ConfigureAwait(false);
        if (endpoint is null || !endpoint.Active)
        {
            return new WebhookTestResult(WebhookTestOutcome.NotFound, null, null, null);
        }

        var now = clock.UtcNow;
        var body = JsonSerializer.Serialize(new
        {
            @event = "webhook.test",
            merchantId,
            at = now,
        }, Json);

        var secret = secrets.Unprotect(endpoint.SecretCipher, endpoint.KeyRingId);
        var signature = RequestSignature.SignWebhook(secret, now.ToUnixTimeSeconds(), body);
        var sent = await sender.SendAsync(endpoint, body, signature, ct).ConfigureAwait(false);

        if (sent.Succeeded)
        {
            await merchants.UpdateWebhookDeliveryAsync(merchantId, now, null, ct).ConfigureAwait(false);
            return new WebhookTestResult(WebhookTestOutcome.Delivered, body, signature, null);
        }

        var failure = sent.FailureReason ?? $"HTTP {sent.StatusCode}";
        await merchants.UpdateWebhookDeliveryAsync(merchantId, null, failure, ct).ConfigureAwait(false);

        return new WebhookTestResult(WebhookTestOutcome.Failed, body, signature, failure);
    }
}

public static class MerchantSecrets
{
    public static string NewSecret()
    {
        Span<byte> bytes = stackalloc byte[32];
        System.Security.Cryptography.RandomNumberGenerator.Fill(bytes);
        return Convert.ToBase64String(bytes);
    }
}

public static class WebhookUrl
{
    public static bool TryNormalize(string? value, out string normalized, out string? reason)
    {
        normalized = "";
        reason = null;

        if (string.IsNullOrWhiteSpace(value))
        {
            reason = "Webhook URL is required.";
            return false;
        }

        if (!Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri)
            || uri.Scheme is not ("https" or "http"))
        {
            reason = "Webhook URL must be absolute HTTP or HTTPS.";
            return false;
        }

        if (string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase)
            || string.Equals(uri.Host, "host.docker.internal", StringComparison.OrdinalIgnoreCase))
        {
            reason = "Webhook URL cannot target a local host.";
            return false;
        }

        if (IPAddress.TryParse(uri.Host, out var ip) && IsBlocked(ip))
        {
            reason = "Webhook URL cannot target a private or loopback address.";
            return false;
        }

        normalized = uri.ToString();
        return true;
    }

    private static bool IsBlocked(IPAddress ip)
    {
        if (IPAddress.IsLoopback(ip)) return true;

        if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
        {
            var bytes = ip.GetAddressBytes();
            return bytes[0] == 10
                || bytes[0] == 127
                || bytes[0] == 0
                || bytes[0] == 169 && bytes[1] == 254
                || bytes[0] == 172 && bytes[1] is >= 16 and <= 31
                || bytes[0] == 192 && bytes[1] == 168;
        }

        return ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || ip.Equals(IPAddress.IPv6Loopback);
    }
}

public enum WebhookOutcome
{
    Created,
    Updated,
    Invalid,
    NoMerchant,
    NotFound,
}

public sealed record WebhookResult(WebhookOutcome Outcome, WebhookEndpoint? Endpoint, string? Reason);

public enum WebhookTestOutcome
{
    Delivered,
    Failed,
    NotFound,
}

public sealed record WebhookTestResult(
    WebhookTestOutcome Outcome,
    string? Body,
    string? Signature,
    string? FailureReason);
