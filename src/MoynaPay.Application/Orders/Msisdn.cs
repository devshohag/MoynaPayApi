namespace MoynaPay.Application.Orders;

/// <summary>
/// Turns whatever a shop stored into something dialable, or refuses.
///
/// Shops store numbers the way customers type them: 01711223344, +8801711223344,
/// 8801711-223344, with spaces, with a stray letter from a paste. Taking the raw string
/// works often enough to be dangerous - it fails on a minority of orders, silently, and
/// those look afterwards like customers who never answered.
///
/// Refusing is the feature. A number nobody can read is an order for a person to look at,
/// not a digit string to improvise with.
/// </summary>
public static class Msisdn
{
    /// <summary>International form without a plus (8801XXXXXXXXX), or null.</summary>
    public static string? Normalise(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;

        var digits = new string(raw.Where(char.IsDigit).ToArray());

        // 00 is the other way of writing +. Stripped first, or 008801… reads as a local
        // number beginning 0088.
        if (digits.StartsWith("00", StringComparison.Ordinal)) digits = digits[2..];

        var national = digits switch
        {
            { Length: 13 } when digits.StartsWith("8801", StringComparison.Ordinal) => digits[2..],
            { Length: 11 } when digits.StartsWith("01", StringComparison.Ordinal) => digits,

            // The leading zero lost somewhere in a spreadsheet, which happens constantly.
            { Length: 10 } when digits.StartsWith('1') => "0" + digits,
            _ => null,
        };

        if (national is null) return null;

        // A landline or a short code is not a typo worth auto-correcting; it is an order
        // whose phone number needs a human being to look at it.
        var known = national[..3] is "013" or "014" or "015" or "016" or "017" or "018" or "019";

        return known ? "88" + national : null;
    }

    /// <summary>01711-223344 for display. Never used for dialling.</summary>
    public static string Pretty(string normalised)
    {
        ArgumentNullException.ThrowIfNull(normalised);

        if (normalised.Length != 13 || !normalised.StartsWith("88", StringComparison.Ordinal))
        {
            return normalised;
        }

        var local = normalised[2..];

        return $"{local[..5]}-{local[5..]}";
    }
}
