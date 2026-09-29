using System.Net;

namespace MoynaPay.Application.Outbox;

/// <summary>
/// Where a delivery is allowed to go.
///
/// The URL comes from the merchant, and a merchant is not an attacker until one of their
/// accounts is taken. Without this check, "my webhook is http://10.0.0.5:6379/" turns our
/// own dispatcher into a way to reach machines on our network that are not exposed to the
/// internet at all - and it reports back the status code, which is enough to map what is
/// there. It costs one function to refuse.
///
/// Refusing at delivery time rather than only at registration time is deliberate: a name
/// that resolved to a public address on the day it was registered can be repointed later.
/// </summary>
public static class WebhookUrl
{
    /// <summary>Null when the URL is fine; otherwise why it was refused, in a shop's words.</summary>
    public static string? Refuse(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return "no webhook url";

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return "webhook url is not a valid absolute url";
        }

        if (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp)
        {
            return $"webhook url scheme '{uri.Scheme}' is not allowed";
        }

        if (uri.HostNameType is UriHostNameType.IPv4 or UriHostNameType.IPv6)
        {
            return IPAddress.TryParse(uri.Host, out var ip) && IsPrivate(ip)
                ? "webhook url points at a private address"
                : null;
        }

        // A name, not a literal. Only the obvious local ones are refused here; a name that
        // resolves to a private address is caught by the sender, which is where resolution
        // actually happens and where the answer cannot go stale between check and connect.
        var host = uri.Host;

        return host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".local", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".internal", StringComparison.OrdinalIgnoreCase)
                ? "webhook url points at a local address"
                : null;
    }

    /// <summary>
    /// Loopback, link-local, and the three private IPv4 ranges - plus their IPv6 forms,
    /// because ::ffff:10.0.0.1 is the same machine written differently and a check that
    /// only reads IPv4 is a check somebody walks around in an afternoon.
    /// </summary>
    public static bool IsPrivate(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);

        if (IPAddress.IsLoopback(address)) return true;

        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();

        if (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
        {
            var b = address.GetAddressBytes();

            return b[0] switch
            {
                0 => true,                                   // 0.0.0.0/8
                10 => true,                                  // 10.0.0.0/8
                127 => true,                                 // loopback
                169 => b[1] == 254,                          // 169.254.0.0/16 link-local
                172 => b[1] >= 16 && b[1] <= 31,             // 172.16.0.0/12
                192 => b[1] == 168,                          // 192.168.0.0/16
                _ => false,
            };
        }

        if (address.IsIPv6LinkLocal || address.IsIPv6SiteLocal) return true;

        // fc00::/7, the IPv6 unique local addresses.
        return (address.GetAddressBytes()[0] & 0xFE) == 0xFC;
    }
}
