using MoynaPay.Application.Abstractions;
using MoynaPay.Application.Security;

namespace MoynaPay.Api;

/// <summary>
/// Establishes which merchant a call to /v1/ came from, or refuses it.
///
/// Three things have to hold, in this order, and each one is cheap before the next:
/// the headers are present, the clock is close enough and the MAC matches, and the nonce
/// has not been used. Checking the nonce first would let anyone fill the replay store
/// with garbage; checking the MAC first costs a hash on every forged request but bounds
/// what an unauthenticated caller can make us remember.
/// </summary>
public sealed class SignedRequestMiddleware(RequestDelegate next, ILogger<SignedRequestMiddleware> log)
{
    public const string MerchantItem = "merchantId";
    public const string KeyItem = "keyId";

    public async Task InvokeAsync(
        HttpContext context, IMerchantStore merchants, INonceStore nonces,
        ISecretProtector protector, IClock clock)
    {
        ArgumentNullException.ThrowIfNull(context);

        var path = context.Request.Path.Value ?? "";

        if (!path.StartsWith("/v1/", StringComparison.Ordinal)
            || path == "/v1/health"
            || path.StartsWith("/v1/merchants", StringComparison.Ordinal))
        {
            await next(context).ConfigureAwait(false);
            return;
        }

        var keyId = context.Request.Headers[RequestSignature.KeyHeader].ToString();
        var nonce = context.Request.Headers[RequestSignature.NonceHeader].ToString();
        var presented = context.Request.Headers[RequestSignature.SignatureHeader].ToString();
        var stamp = context.Request.Headers[RequestSignature.TimestampHeader].ToString();

        if (string.IsNullOrEmpty(keyId) || string.IsNullOrEmpty(nonce)
            || string.IsNullOrEmpty(presented) || !long.TryParse(stamp, out var timestamp))
        {
            await RefuseAsync(context, "missing signing headers").ConfigureAwait(false);
            return;
        }

        var credential = await merchants.FindCredentialAsync(keyId).ConfigureAwait(false);
        if (credential is null)
        {
            await RefuseAsync(context, "unknown or revoked key").ConfigureAwait(false);
            return;
        }

        // Buffered so the body can be read for the MAC and then again by the handler.
        // The MAC is over the bytes as they arrived: re-serialising would change them.
        context.Request.EnableBuffering();
        using var reader = new StreamReader(context.Request.Body, leaveOpen: true);
        var body = await reader.ReadToEndAsync().ConfigureAwait(false);
        context.Request.Body.Position = 0;

        var secret = protector.Unprotect(credential.SecretCipher, credential.KeyRingId);
        var now = clock.UtcNow.ToUnixTimeSeconds();

        var ok = RequestSignature.Verify(
            secret, context.Request.Method,
            context.Request.Path + context.Request.QueryString,
            timestamp, nonce, body, presented, now);

        if (!ok)
        {
            await RefuseAsync(context, "bad signature or stale timestamp").ConfigureAwait(false);
            return;
        }

        if (!await nonces.TryUseAsync(keyId, nonce).ConfigureAwait(false))
        {
            await RefuseAsync(context, "nonce already used").ConfigureAwait(false);
            return;
        }

        context.Items[MerchantItem] = credential.TenantId;
        context.Items[KeyItem] = keyId;

        await next(context).ConfigureAwait(false);
    }

    private async Task RefuseAsync(HttpContext context, string reason)
    {
        // Logged in full, answered in one word. Telling a caller which part of their
        // signature was wrong tells an attacker the same thing, one guess at a time.
        log.LogInformation("Refused {Method} {Path}: {Reason}",
            context.Request.Method, context.Request.Path, reason);

        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        await context.Response.WriteAsJsonAsync(new { error = "unauthorized" }).ConfigureAwait(false);
    }
}
