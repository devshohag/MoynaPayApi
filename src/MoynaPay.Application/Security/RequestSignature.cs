using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace MoynaPay.Application.Security;

/// <summary>
/// How a shop proves a request came from it, and how MoynaPay proves a delivery came from
/// MoynaPay.
///
/// Byte for byte the scheme YoPay uses, with different header names. That is deliberate:
/// a merchant who has already integrated YoPay has written the hard part, and the PHP,
/// .NET and Node clients work against both with a different base URL and prefix.
/// Inventing a second scheme would cost every integrator an afternoon and buy nothing.
///
/// The two signatures are different shapes because they answer different questions.
///
/// A REQUEST signature proves a call came from a known shop, so it covers the method and
/// the path as well as the body - without them a signed GET can be replayed as a DELETE,
/// and a signature for one order authorises another. It carries a nonce the server
/// refuses to see twice.
///
/// A WEBHOOK signature proves a delivery came from us. There is no nonce: the receiving
/// shop has nowhere to remember one, so replay is bounded by the timestamp inside the MAC
/// and by their own idempotency.
///
/// Hex case is not significant on either side.
/// </summary>
public static class RequestSignature
{
    public const string KeyHeader = "X-MoynaPay-Key";
    public const string TimestampHeader = "X-MoynaPay-Timestamp";
    public const string NonceHeader = "X-MoynaPay-Nonce";
    public const string SignatureHeader = "X-MoynaPay-Signature";
    public const string EventHeader = "X-MoynaPay-Event";
    public const string DeliveryHeader = "X-MoynaPay-Delivery";

    /// <summary>How far apart the clocks may be, in seconds.</summary>
    public const int ToleranceSeconds = 300;

    /// <summary>
    /// The exact string a request signature covers.
    ///
    /// The body is hashed rather than concatenated so the shape stays fixed whatever the
    /// body is, and so reproducing it in another language is mechanical rather than a
    /// matter of interpretation. Upper-case hex for the body digest is not cosmetic - it
    /// is what gets signed.
    /// </summary>
    public static string Canonical(
        string method, string pathAndQuery, long timestamp, string nonce, string body)
    {
        ArgumentNullException.ThrowIfNull(method);

        return string.Join('\n',
            method.ToUpperInvariant(),
            pathAndQuery,
            timestamp.ToString(CultureInfo.InvariantCulture),
            nonce,
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(body ?? ""))));
    }

    public static string Sign(
        string secret, string method, string pathAndQuery, long timestamp, string nonce, string body) =>
        Hmac(secret, Canonical(method, pathAndQuery, timestamp, nonce, body));

    /// <summary>
    /// Checks a request. The nonce is NOT checked here - that needs a store, and mixing a
    /// pure function with one that remembers makes both harder to test. The caller checks
    /// the nonce after this returns true.
    /// </summary>
    public static bool Verify(
        string secret, string method, string pathAndQuery, long timestamp, string nonce,
        string body, string? presented, long nowSeconds)
    {
        if (Math.Abs(nowSeconds - timestamp) > ToleranceSeconds) return false;

        return FixedTimeEquals(Sign(secret, method, pathAndQuery, timestamp, nonce, body), presented);
    }

    /// <summary>The X-MoynaPay-Signature header on an outgoing delivery.</summary>
    public static string SignWebhook(string secret, long timestamp, string body) =>
        $"t={timestamp.ToString(CultureInfo.InvariantCulture)},v1={Hmac(secret, $"{timestamp}.{body}")}";

    /// <summary>
    /// Checks a delivery. The body must be the RAW bytes as received - parsing the JSON
    /// and encoding it again changes them, and the MAC is over bytes. That one mistake
    /// accounts for most reports that "the signature never verifies".
    /// </summary>
    public static bool VerifyWebhook(string secret, string header, string body, long nowSeconds)
    {
        if (string.IsNullOrEmpty(secret) || string.IsNullOrEmpty(header)) return false;

        string? t = null, v1 = null;

        foreach (var piece in header.Split(','))
        {
            var at = piece.IndexOf('=', StringComparison.Ordinal);
            if (at <= 0) continue;

            var name = piece[..at].Trim();
            var value = piece[(at + 1)..].Trim();

            if (name == "t") t = value;
            else if (name == "v1") v1 = value;
        }

        if (t is null || v1 is null || !long.TryParse(t, out var timestamp)) return false;

        // Checked before the MAC. Skip it and the signature never expires, which turns any
        // captured "paid" delivery into one that can be replayed forever.
        if (Math.Abs(nowSeconds - timestamp) > ToleranceSeconds) return false;

        return FixedTimeEquals(Hmac(secret, $"{t}.{body}"), v1);
    }

    private static string Hmac(string secret, string message) =>
        Convert.ToHexString(
            HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(message)));

    /// <summary>
    /// Constant time. An ordinary comparison leaks how much of a forged MAC was right,
    /// which is enough to build the rest of it one byte at a time.
    /// </summary>
    private static bool FixedTimeEquals(string mine, string? presented)
    {
        if (string.IsNullOrEmpty(presented) || presented.Length != mine.Length) return false;

        return CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(mine.ToLowerInvariant()),
            Encoding.ASCII.GetBytes(presented.ToLowerInvariant()));
    }
}
