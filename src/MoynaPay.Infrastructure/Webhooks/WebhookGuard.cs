using System.Net;
using System.Net.Sockets;

namespace MoynaPay.Infrastructure.Webhooks;

/// <summary>
/// The half of the webhook URL check that text cannot do.
///
/// WebhookService refuses a URL whose host is written as a private IP literal. That stops
/// the careless case and none of the deliberate one: a host name is resolved when the
/// connection is made, so "webhook.theirshop.com" pointing at 10.0.0.5 passes every check
/// that reads a string, and a shop that answers 302 walks past it a second way.
///
/// Both are closed here, at the only point where the answer cannot go stale between the
/// check and the connection - because this IS the connection.
///
/// It matters because the delivery result is reported back to the merchant. "No such host"
/// against "connection refused" against "HTTP 403" is enough to map what is listening on
/// an internal network, one webhook test at a time.
/// </summary>
public static class WebhookGuard
{
    public static HttpMessageHandler Handler() => new SocketsHttpHandler
    {
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),

        // A shop that answers 302 Location: http://169.254.169.254/ would otherwise be
        // followed. The status code is reported to the merchant instead, which is
        // something they can act on.
        AllowAutoRedirect = false,

        ConnectCallback = static async (context, ct) =>
        {
            var host = context.DnsEndPoint.Host;

            var addresses = await Dns.GetHostAddressesAsync(host, ct).ConfigureAwait(false);
            var allowed = Array.FindAll(addresses, a => !IsBlocked(a));

            if (allowed.Length == 0)
            {
                throw new HttpRequestException(
                    HttpRequestError.ConnectionError,
                    $"'{host}' resolves only to addresses that are not allowed");
            }

            var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };

            try
            {
                await socket.ConnectAsync(allowed, context.DnsEndPoint.Port, ct).ConfigureAwait(false);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        },
    };

    /// <summary>
    /// Loopback, link-local and the private ranges - including their IPv6-mapped forms,
    /// because ::ffff:10.0.0.1 is the same machine written differently and a check that
    /// only reads IPv4 is one somebody walks around in an afternoon.
    /// </summary>
    public static bool IsBlocked(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);

        if (IPAddress.IsLoopback(address)) return true;

        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();

        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = address.GetAddressBytes();

            return b[0] switch
            {
                0 => true,                        // 0.0.0.0/8
                10 => true,                       // 10.0.0.0/8
                127 => true,                      // loopback
                169 => b[1] == 254,               // link-local, and the cloud metadata address
                172 => b[1] is >= 16 and <= 31,   // 172.16.0.0/12
                192 => b[1] == 168,               // 192.168.0.0/16
                _ => false,
            };
        }

        if (address.IsIPv6LinkLocal || address.IsIPv6SiteLocal) return true;

        // fc00::/7, the IPv6 unique local addresses.
        return (address.GetAddressBytes()[0] & 0xFE) == 0xFC;
    }
}
