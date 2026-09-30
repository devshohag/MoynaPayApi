using MoynaPay.Application.Abstractions;
using MoynaPay.Domain.Orders;

namespace MoynaPay.Application.Voice;

public sealed record ResolvedTrunk(string CallerId, string Endpoint);

public sealed record TelephonyRoutingOptions(
    string SharedCallerId,
    string SharedTrunkName,
    string? DevSoftphoneEndpoint,
    string? DialEndpointTemplate);

public sealed class TrunkResolver(ISipTrunkStore trunks, TelephonyRoutingOptions options)
{
    public async Task<ResolvedTrunk> ResolveAsync(Order order, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(order);

        if (!string.IsNullOrWhiteSpace(options.DevSoftphoneEndpoint))
        {
            return new ResolvedTrunk(options.SharedCallerId, options.DevSoftphoneEndpoint);
        }

        var trunk = await trunks
            .FindActiveForMerchantAsync(order.TenantId, ct).ConfigureAwait(false);

        if (trunk is not null)
        {
            return new ResolvedTrunk(
                trunk.CallerId,
                Endpoint(order.Msisdn, trunk.Host));
        }

        if (!string.IsNullOrWhiteSpace(options.DialEndpointTemplate))
        {
            return new ResolvedTrunk(
                options.SharedCallerId,
                options.DialEndpointTemplate.Replace("{msisdn}", order.Msisdn, StringComparison.Ordinal));
        }

        return new ResolvedTrunk(
            options.SharedCallerId,
            Endpoint(order.Msisdn, options.SharedTrunkName));
    }

    private static string Endpoint(string msisdn, string trunkName) =>
        $"PJSIP/{msisdn}@{trunkName}";
}
