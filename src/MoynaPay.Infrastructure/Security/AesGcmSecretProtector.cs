using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Configuration;
using MoynaPay.Application.Abstractions;
using MoynaPay.Infrastructure.Memory;

namespace MoynaPay.Infrastructure.Security;

/// <summary>
/// Versioned AES-GCM key ring for secrets the server has to open again.
///
/// The active key encrypts new secrets; every configured key can decrypt old ones. That
/// is the rotation property: add a new key, make it active, and old rows keep working
/// until a later migration rewrites them.
/// </summary>
public sealed class AesGcmSecretProtector : ISecretProtector
{
    public const string Format = "mpaes1";
    public const string DefaultDevelopmentKeyId = "dev-aes-gcm";

    private const int NonceSize = 12;
    private const int TagSize = 16;
    private readonly string _activeKeyId;
    private readonly IReadOnlyDictionary<string, byte[]> _keys;

    public AesGcmSecretProtector(string activeKeyId, IReadOnlyDictionary<string, byte[]> keys)
    {
        if (string.IsNullOrWhiteSpace(activeKeyId))
        {
            throw new ArgumentException("An active key id is required.", nameof(activeKeyId));
        }

        if (keys.Count == 0)
        {
            throw new ArgumentException("At least one key is required.", nameof(keys));
        }

        _keys = keys.ToDictionary(
            pair => pair.Key,
            pair => ValidateKey(pair.Key, pair.Value),
            StringComparer.Ordinal);

        if (!_keys.ContainsKey(activeKeyId))
        {
            throw new ArgumentException("The active key must exist in the key ring.", nameof(activeKeyId));
        }

        _activeKeyId = activeKeyId;
    }

    public static AesGcmSecretProtector FromConfiguration(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var section = configuration.GetSection("MoynaPay:Secrets");
        var configured = section.GetSection("Keys").GetChildren()
            .Where(child => !string.IsNullOrWhiteSpace(child.Key) && !string.IsNullOrWhiteSpace(child.Value))
            .Select(child => new KeyValuePair<string, byte[]>(child.Key, Convert.FromBase64String(child.Value!)))
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);

        if (configured.Count == 0)
        {
            configured[DefaultDevelopmentKeyId] = Convert.FromBase64String(
                "VJHnI7xMTZzOq9ehUsq2bkt88AqXlHLR99OOOp6toR0=");
        }

        var active = section["ActiveKeyId"];
        if (string.IsNullOrWhiteSpace(active))
        {
            active = configured.Keys.Order(StringComparer.Ordinal).Last();
        }

        return new AesGcmSecretProtector(active, configured);
    }

    public string Protect(string plaintext, out string keyRingId)
    {
        ArgumentNullException.ThrowIfNull(plaintext);

        keyRingId = _activeKeyId;

        var key = _keys[_activeKeyId];
        Span<byte> nonce = stackalloc byte[NonceSize];
        RandomNumberGenerator.Fill(nonce);

        var plain = Encoding.UTF8.GetBytes(plaintext);
        var cipher = new byte[plain.Length];
        Span<byte> tag = stackalloc byte[TagSize];

        using (var aes = new AesGcm(key, TagSize))
        {
            aes.Encrypt(nonce, plain, cipher, tag, Encoding.UTF8.GetBytes(_activeKeyId));
        }

        return string.Join('.',
            Format,
            _activeKeyId,
            Base64Url(nonce),
            Base64Url(cipher),
            Base64Url(tag));
    }

    public string Unprotect(string cipher, string keyRingId)
    {
        ArgumentNullException.ThrowIfNull(cipher);

        var pieces = cipher.Split('.');
        if (pieces.Length != 5 || pieces[0] != Format)
        {
            throw new CryptographicException("Secret cipher format is not supported.");
        }

        var id = pieces[1];
        if (!string.IsNullOrWhiteSpace(keyRingId) && !string.Equals(id, keyRingId, StringComparison.Ordinal))
        {
            throw new CryptographicException("Secret key id does not match the cipher text.");
        }

        if (!_keys.TryGetValue(id, out var key))
        {
            throw new CryptographicException("Secret key is not in the key ring.");
        }

        var nonce = FromBase64Url(pieces[2]);
        var encrypted = FromBase64Url(pieces[3]);
        var tag = FromBase64Url(pieces[4]);
        var plain = new byte[encrypted.Length];

        if (nonce.Length != NonceSize || tag.Length != TagSize)
        {
            throw new CryptographicException("Secret cipher is not valid.");
        }

        using (var aes = new AesGcm(key, TagSize))
        {
            aes.Decrypt(nonce, encrypted, tag, plain, Encoding.UTF8.GetBytes(id));
        }

        return Encoding.UTF8.GetString(plain);
    }

    private static byte[] ValidateKey(string keyId, byte[] key)
    {
        if (string.IsNullOrWhiteSpace(keyId))
        {
            throw new ArgumentException("Key id is required.", nameof(keyId));
        }

        if (key.Length is not (16 or 24 or 32))
        {
            throw new ArgumentException("AES-GCM keys must be 16, 24 or 32 bytes.", nameof(key));
        }

        return key.ToArray();
    }

    private static string Base64Url(ReadOnlySpan<byte> bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] FromBase64Url(string value)
    {
        var padded = value.Replace('-', '+').Replace('_', '/');
        padded = padded.PadRight(padded.Length + ((4 - padded.Length % 4) % 4), '=');
        return Convert.FromBase64String(padded);
    }
}

public static class SecretCipherMigration
{
    public static int MigratePlaintext(MemoryDatabase db, ISecretProtector protector)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(protector);

        var count = 0;

        lock (db.Gate)
        {
            foreach (var credential in db.Credentials.Values)
            {
                if (credential.KeyRingId != PlaintextSecretProtector.KeyRing) continue;

                credential.SecretCipher = protector.Protect(credential.SecretCipher, out var keyRingId);
                credential.KeyRingId = keyRingId;
                credential.UpdatedAt = DateTimeOffset.UtcNow;
                count++;
            }

            foreach (var webhook in db.Webhooks.Values)
            {
                if (webhook.KeyRingId != PlaintextSecretProtector.KeyRing) continue;

                webhook.SecretCipher = protector.Protect(webhook.SecretCipher, out var keyRingId);
                webhook.KeyRingId = keyRingId;
                webhook.UpdatedAt = DateTimeOffset.UtcNow;
                count++;
            }
        }

        return count;
    }
}
