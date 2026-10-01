using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace MoynaPay.Application.Payments.Ingestion;

/// <summary>
/// Identifies one handset observation. The server computes this from the fields it stores;
/// a device-supplied hash could make different messages collide or one message look new.
/// </summary>
public static class DedupeHash
{
    public static string Compute(
        Guid deviceId, string senderId, string body, DateTimeOffset deviceReceivedAt)
    {
        ArgumentNullException.ThrowIfNull(senderId);
        ArgumentNullException.ThrowIfNull(body);

        var material = string.Join('|',
            deviceId.ToString("N"),
            senderId.Trim().ToUpperInvariant(),
            NormaliseBody(body),
            deviceReceivedAt.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture));

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material)));
    }

    private static string NormaliseBody(string body)
    {
        var builder = new StringBuilder(body.Length);
        var lastWasSpace = false;

        foreach (var c in body.Trim())
        {
            if (char.IsWhiteSpace(c))
            {
                if (!lastWasSpace)
                {
                    builder.Append(' ');
                }

                lastWasSpace = true;
            }
            else
            {
                builder.Append(c);
                lastWasSpace = false;
            }
        }

        return builder.ToString();
    }
}
