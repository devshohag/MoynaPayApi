using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace MoynaPay.Application.Voice.Recording;

public sealed class RecordingPlaybackUrlSigner(
    string secret,
    string publicBasePath = "/v1/recordings/play")
{
    public string Sign(string objectStorageKey, DateTimeOffset expiresAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(objectStorageKey);

        var expires = expiresAt.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        var signature = Base64Url(HMACSHA256.HashData(
            Encoding.UTF8.GetBytes(secret),
            Encoding.UTF8.GetBytes($"{objectStorageKey}\n{expires}")));

        return $"{publicBasePath}?key={Uri.EscapeDataString(objectStorageKey)}" +
               $"&expires={expires}&sig={signature}";
    }

    public bool Verify(string objectStorageKey, long expires, string? signature, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(objectStorageKey) || string.IsNullOrWhiteSpace(signature))
            return false;
        if (expires < now.ToUnixTimeSeconds())
            return false;

        var expected = Base64Url(HMACSHA256.HashData(
            Encoding.UTF8.GetBytes(secret),
            Encoding.UTF8.GetBytes($"{objectStorageKey}\n{expires.ToString(CultureInfo.InvariantCulture)}")));

        if (signature.Length != expected.Length)
            return false;

        return CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(expected),
            Encoding.ASCII.GetBytes(signature));
    }

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
