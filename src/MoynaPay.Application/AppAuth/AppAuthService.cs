using System.Security.Cryptography;
using System.Text;
using MoynaPay.Application.Abstractions;
using MoynaPay.Application.Orders;
using MoynaPay.Domain.Merchants;

namespace MoynaPay.Application.AppAuth;

public sealed class AppAuthService(
    IMerchantStore merchants,
    IAppAuthStore auth,
    IRateLimitStore rateLimits,
    IClock clock)
{
    public const int OtpDigits = 6;
    public static readonly TimeSpan OtpTtl = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan OtpRateWindow = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan AccessTtl = TimeSpan.FromMinutes(15);
    public static readonly TimeSpan RefreshTtl = TimeSpan.FromDays(30);

    public async Task<RequestOtpResult> RequestOtpAsync(string? phone, CancellationToken ct = default)
    {
        var msisdn = Msisdn.Normalise(phone);
        if (msisdn is null)
        {
            return new RequestOtpResult(RequestOtpOutcome.Invalid, null, null,
                "A valid phone number is required.");
        }

        var now = clock.UtcNow;
        if (!await rateLimits.TryConsumeAsync($"app:otp:{msisdn}", now, OtpRateWindow, 3, ct)
                .ConfigureAwait(false))
        {
            return new RequestOtpResult(RequestOtpOutcome.RateLimited, null, null,
                "Too many OTP requests. Try again later.");
        }

        var merchant = await merchants.FindByMsisdnAsync(msisdn, ct).ConfigureAwait(false);
        if (merchant is null)
        {
            return new RequestOtpResult(RequestOtpOutcome.NoMerchant, null, null,
                "No merchant account uses this phone.");
        }

        var code = NewOtp();
        await auth.SaveOtpAsync(new AppOtpChallenge
        {
            Id = Guid.CreateVersion7(),
            MerchantId = merchant.Id,
            Msisdn = msisdn,
            CodeHash = Hash($"{msisdn}:{code}"),
            ExpiresAt = now.Add(OtpTtl),
            CreatedAt = now,
        }, ct).ConfigureAwait(false);

        return new RequestOtpResult(RequestOtpOutcome.Sent, merchant, code, null);
    }

    public async Task<AppTokenResult> VerifyOtpAsync(string? phone, string? code,
        CancellationToken ct = default)
    {
        var msisdn = Msisdn.Normalise(phone);
        var cleanedCode = CleanCode(code);
        if (msisdn is null || cleanedCode is null)
        {
            return new AppTokenResult(AppTokenOutcome.Invalid, null, null, null, "Invalid OTP.");
        }

        var now = clock.UtcNow;
        if (!await rateLimits.TryConsumeAsync($"app:otp-verify:{msisdn}", now, OtpRateWindow, 5, ct)
                .ConfigureAwait(false))
        {
            return new AppTokenResult(AppTokenOutcome.RateLimited, null, null, null,
                "Too many OTP attempts. Try again later.");
        }

        var challenge = await auth.LatestOtpAsync(msisdn, ct).ConfigureAwait(false);
        if (challenge is null || challenge.ConsumedAt is not null || challenge.ExpiresAt <= now)
        {
            return new AppTokenResult(AppTokenOutcome.Invalid, null, null, null, "Invalid OTP.");
        }

        if (challenge.FailedAttempts >= 5)
        {
            return new AppTokenResult(AppTokenOutcome.RateLimited, null, null, null,
                "Too many OTP attempts. Try again later.");
        }

        if (!FixedEquals(challenge.CodeHash, Hash($"{msisdn}:{cleanedCode}")))
        {
            challenge.FailedAttempts++;
            await auth.SaveOtpAsync(challenge, ct).ConfigureAwait(false);
            return new AppTokenResult(AppTokenOutcome.Invalid, null, null, null, "Invalid OTP.");
        }

        challenge.ConsumedAt = now;
        await auth.SaveOtpAsync(challenge, ct).ConfigureAwait(false);

        return await IssueTokensAsync(challenge.MerchantId, now, ct).ConfigureAwait(false);
    }

    public async Task<AppTokenResult> RefreshAsync(string? refreshToken, CancellationToken ct = default)
    {
        var hash = TokenHash(refreshToken);
        if (hash is null)
        {
            return new AppTokenResult(AppTokenOutcome.Invalid, null, null, null, "Invalid refresh token.");
        }

        var now = clock.UtcNow;
        var existing = await auth.FindTokenAsync(hash, AppTokenKind.Refresh, ct).ConfigureAwait(false);
        if (existing is null || !existing.IsUsable(now))
        {
            return new AppTokenResult(AppTokenOutcome.Invalid, null, null, null, "Invalid refresh token.");
        }

        var next = await IssueTokensAsync(existing.MerchantId, now, ct).ConfigureAwait(false);
        await auth.RevokeTokenAsync(hash, AppTokenKind.Refresh, now,
            TokenHash(next.RefreshToken), ct).ConfigureAwait(false);

        return next;
    }

    public async Task<LogoutResult> LogoutAsync(string? refreshToken, CancellationToken ct = default)
    {
        var hash = TokenHash(refreshToken);
        if (hash is null) return new LogoutResult(false);

        await auth.RevokeTokenAsync(hash, AppTokenKind.Refresh, clock.UtcNow, ct: ct)
            .ConfigureAwait(false);

        return new LogoutResult(true);
    }

    public async Task<AppPrincipal?> ValidateAccessAsync(string? accessToken,
        CancellationToken ct = default)
    {
        var hash = TokenHash(accessToken);
        if (hash is null) return null;

        var token = await auth.FindTokenAsync(hash, AppTokenKind.Access, ct).ConfigureAwait(false);
        if (token is null || !token.IsUsable(clock.UtcNow)) return null;

        var merchant = await merchants.FindAsync(token.MerchantId, ct).ConfigureAwait(false);
        return merchant is null ? null : new AppPrincipal(merchant.Id, merchant.Name, merchant.Msisdn);
    }

    private async Task<AppTokenResult> IssueTokensAsync(Guid merchantId, DateTimeOffset now,
        CancellationToken ct)
    {
        var access = NewToken();
        var refresh = NewToken();

        await auth.SaveTokenAsync(Token(merchantId, access, AppTokenKind.Access, now, AccessTtl), ct)
            .ConfigureAwait(false);
        await auth.SaveTokenAsync(Token(merchantId, refresh, AppTokenKind.Refresh, now, RefreshTtl), ct)
            .ConfigureAwait(false);

        return new AppTokenResult(AppTokenOutcome.Issued, merchantId, access, refresh, null);
    }

    private static AppToken Token(Guid merchantId, string token, AppTokenKind kind,
        DateTimeOffset now, TimeSpan ttl) => new()
    {
        Id = Guid.CreateVersion7(),
        MerchantId = merchantId,
        TokenHash = Hash(token),
        Kind = kind,
        ExpiresAt = now.Add(ttl),
        CreatedAt = now,
    };

    private static string NewOtp() =>
        RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6");

    private static string NewToken()
    {
        Span<byte> bytes = stackalloc byte[32];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToBase64String(bytes)
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');
    }

    public static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    public static string? TokenHash(string? token) =>
        string.IsNullOrWhiteSpace(token) ? null : Hash(token.Trim());

    private static string? CleanCode(string? code)
    {
        if (string.IsNullOrWhiteSpace(code)) return null;
        var cleaned = new string(code.Where(char.IsDigit).ToArray());
        return cleaned.Length == OtpDigits ? cleaned : null;
    }

    private static bool FixedEquals(string left, string right) =>
        CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(left), Encoding.UTF8.GetBytes(right));
}

public enum RequestOtpOutcome
{
    Sent,
    Invalid,
    NoMerchant,
    RateLimited,
}

public sealed record RequestOtpResult(
    RequestOtpOutcome Outcome,
    Merchant? Merchant,
    string? DevOtp,
    string? Reason);

public enum AppTokenOutcome
{
    Issued,
    Invalid,
    RateLimited,
}

public sealed record AppTokenResult(
    AppTokenOutcome Outcome,
    Guid? MerchantId,
    string? AccessToken,
    string? RefreshToken,
    string? Reason);

public sealed record LogoutResult(bool Accepted);

public sealed record AppPrincipal(Guid MerchantId, string MerchantName, string Msisdn);
