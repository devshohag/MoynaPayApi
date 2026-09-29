using System.Net;
using Microsoft.Extensions.Configuration;
using MoynaPay.Application.Abstractions;
using MoynaPay.Application.Merchants;
using MoynaPay.Application.Security;
using MoynaPay.Application.Orders;
using MoynaPay.Infrastructure.Memory;
using MoynaPay.Infrastructure.Security;
using MoynaPay.Infrastructure.Webhooks;

namespace MoynaPay.Check;

/// <summary>
/// The holes found in the review of 29 September, and the assertions that keep them shut.
///
/// Each one of these failed before the fix. That is the only property that makes a check
/// worth its line: a suite that would have passed either way proves nothing about the
/// thing it claims to guard.
/// </summary>
internal static class Hardening
{
    public static async Task RunAsync(
        Action<string, Func<bool>> check, Func<string, Func<Task<bool>>, Task> checkAsync)
    {
        ArgumentNullException.ThrowIfNull(check);
        ArgumentNullException.ThrowIfNull(checkAsync);

        // -------------------------------------------------------------------
        // The unsigned onboarding surface
        //
        // The prefix exemption used to cover "/v1/merchants" and everything under it,
        // which included the route that issues a merchant's signing secret. A merchant id
        // is not a credential - it sits in URLs, in logs, and in the body of a
        // webhook.test posted to somebody else's server.
        // -------------------------------------------------------------------
        check("onboarding is the only unsigned route", () =>
            OperatorRoutes.IsOperatorOnly("/v1/merchants"));

        check("bootstrap key issue is operator-gated, not public", () =>
            OperatorRoutes.IsOperatorOnly(
                "/v1/merchants/0199aaaa-bbbb-cccc-dddd-eeeeeeeeeeee/api-keys"));

        check("reading a merchant still needs a signature", () =>
            !OperatorRoutes.IsOperatorOnly(
                "/v1/merchants/0199aaaa-bbbb-cccc-dddd-eeeeeeeeeeee"));

        check("orders still need a signature", () =>
            !OperatorRoutes.IsOperatorOnly("/v1/orders"));

        check("a path that merely contains api-keys is not exempt", () =>
            !OperatorRoutes.IsOperatorOnly("/v1/orders/api-keys/x"));

        // -------------------------------------------------------------------
        // The key ring
        // -------------------------------------------------------------------
        check("no configured key ring refuses to start outside development", () =>
        {
            try
            {
                AesGcmSecretProtector.FromConfiguration(Empty(), isDevelopment: false);
                return false;
            }
            catch (InvalidOperationException)
            {
                return true;
            }
        });

        check("a laptop still starts with the development key", () =>
            AesGcmSecretProtector.FromConfiguration(Empty(), isDevelopment: true) is not null);

        check("a configured key ring is used in production", () =>
        {
            var protector = AesGcmSecretProtector.FromConfiguration(
                Config(("MoynaPay:Secrets:Keys:live-1", Convert.ToBase64String(new byte[32]))),
                isDevelopment: false);

            var cipher = protector.Protect("secret", out var keyRingId);

            return keyRingId == "live-1" && protector.Unprotect(cipher, keyRingId) == "secret";
        });

        // -------------------------------------------------------------------
        // Where a delivery may actually connect
        //
        // WebhookService refuses a private IP written as a literal. That is the careless
        // case. These are the deliberate ones: the address a name resolves to.
        // -------------------------------------------------------------------
        check("the cloud metadata address is blocked", () =>
            WebhookGuard.IsBlocked(IPAddress.Parse("169.254.169.254")));

        check("10.x is blocked", () => WebhookGuard.IsBlocked(IPAddress.Parse("10.0.0.5")));
        check("172.20.x is blocked", () => WebhookGuard.IsBlocked(IPAddress.Parse("172.20.0.1")));
        check("172.32.x is public", () => !WebhookGuard.IsBlocked(IPAddress.Parse("172.32.0.1")));
        check("loopback is blocked", () => WebhookGuard.IsBlocked(IPAddress.Parse("127.0.0.1")));
        check("::1 is blocked", () => WebhookGuard.IsBlocked(IPAddress.IPv6Loopback));

        // The same private address, written so a check that only reads IPv4 misses it.
        check("an ipv4-mapped private address is blocked", () =>
            WebhookGuard.IsBlocked(IPAddress.Parse("::ffff:10.0.0.1")));

        check("a public address is allowed", () =>
            !WebhookGuard.IsBlocked(IPAddress.Parse("203.0.113.10")));

        // -------------------------------------------------------------------
        // Review claims
        // -------------------------------------------------------------------
        check("a claim cannot be held past the maximum", () =>
            ReviewQueueService.Clamp(TimeSpan.FromSeconds(2_000_000_000))
                == ReviewQueueService.MaxClaimFor);

        check("a zero claim gets the default", () =>
            ReviewQueueService.Clamp(TimeSpan.Zero) == ReviewQueueService.DefaultClaimFor);

        check("a negative claim gets the default", () =>
            ReviewQueueService.Clamp(TimeSpan.FromSeconds(-1)) == ReviewQueueService.DefaultClaimFor);

        check("a sensible claim is left alone", () =>
            ReviewQueueService.Clamp(TimeSpan.FromMinutes(10)) == TimeSpan.FromMinutes(10));

        // -------------------------------------------------------------------
        // The owner's phone is the app login identity
        // -------------------------------------------------------------------
        await checkAsync("a second merchant cannot take an owner's phone", async () =>
        {
            var service = NewMerchantService();

            var first = await service.CreateAsync(new CreateMerchantCommand
            {
                Name = "First Shop", Msisdn = "01711223344",
            });

            // Written differently, the same number after normalisation - which is how the
            // login resolves it, so this is the form the check has to catch.
            var second = await service.CreateAsync(new CreateMerchantCommand
            {
                Name = "Impostor", Msisdn = "+8801711-22 33 44",
            });

            return first.Outcome == CreateMerchantOutcome.Created
                && second.Outcome == CreateMerchantOutcome.Invalid;
        });

        await checkAsync("a different phone is still accepted", async () =>
        {
            var service = NewMerchantService();

            await service.CreateAsync(new CreateMerchantCommand
            {
                Name = "First Shop", Msisdn = "01711223344",
            });

            var second = await service.CreateAsync(new CreateMerchantCommand
            {
                Name = "Second Shop", Msisdn = "01911223344",
            });

            return second.Outcome == CreateMerchantOutcome.Created;
        });
    }

    private static MerchantService NewMerchantService()
    {
        var db = new MemoryDatabase();

        return new MerchantService(
            new MemoryMerchantStore(db),
            new PlaintextSecretProtector(),
            new SystemClock());
    }

    private static IConfiguration Empty() => Config();

    private static IConfiguration Config(params (string Key, string Value)[] values) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(values.Select(v => new KeyValuePair<string, string?>(v.Key, v.Value)))
            .Build();
}
