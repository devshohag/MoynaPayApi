using System.Security.Cryptography;

namespace MoynaPay.Application.Payments.Invoicing;

/// <summary>
/// The short code a customer can be asked to type into the reference field on a bKash
/// send money, and which appears on the checkout page as the invoice's public name.
///
/// The alphabet leaves out I, O, 0, 1, S and 5. People read these codes off one screen
/// and type them into another, usually on a phone, often in a hurry; the characters that
/// get confused are the ones not to use. Six characters from a 30 letter alphabet is
/// about 729 million combinations, which is far more than a wallet will ever have open at
/// once - the code identifies an invoice, it does not authorise anything.
/// </summary>
public static class ReferenceCode
{
    private const string Alphabet = "ABCDEFGHJKLMNPQRTUVWXYZ2346789";
    public const int Length = 6;
    public const string Prefix = "YP";

    public static string New()
    {
        var chars = new char[Length];

        for (var i = 0; i < Length; i++)
        {
            chars[i] = Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)];
        }

        return Prefix + new string(chars);
    }

    /// <summary>
    /// Normalises what a customer typed before comparing. They will use lower case, add
    /// spaces, and leave the prefix off; none of that should mean a payment goes
    /// unmatched.
    /// </summary>
    public static string Normalise(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return string.Empty;
        }

        var cleaned = new string([.. input
            .ToUpperInvariant()
            .Where(char.IsLetterOrDigit)]);

        return cleaned.StartsWith(Prefix, StringComparison.Ordinal) ? cleaned : Prefix + cleaned;
    }

    public static bool Matches(string code, string? customerTyped) =>
        string.Equals(code, Normalise(customerTyped), StringComparison.Ordinal);
}
