using System.Text.RegularExpressions;

namespace MoynaPay.Application.Payments.Invoicing;

/// <summary>
/// Cleans up what a customer typed into the transaction id box.
///
/// They paste the whole SMS, they copy it with a trailing space, they type it in lower
/// case, and on a phone keyboard they will put spaces in the middle of it. Every one of
/// those is a payment that would otherwise sit unmatched while the customer swears they
/// paid - so the cleaning happens here, once, rather than in three places that disagree.
///
/// Extracting from a pasted message is deliberate and safe: the pattern is anchored to
/// ten uppercase alphanumerics, and a message that contains none of those yields nothing
/// rather than a guess.
/// </summary>
public static partial class TrxIdInput
{
    public const int Length = 10;

    [GeneratedRegex(@"\b[A-Z0-9]{10}\b", RegexOptions.CultureInvariant)]
    private static partial Regex TrxIdPattern();

    /// <summary>Returns the normalised id, or null when there is nothing usable.</summary>
    public static string? Normalise(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return null;
        }

        var upper = input.ToUpperInvariant();

        // Typed by hand: strip whatever separators the phone keyboard added.
        var compact = new string([.. upper.Where(char.IsLetterOrDigit)]);
        if (compact.Length == Length)
        {
            return compact;
        }

        // Pasted from the message: pull the one token that looks like an id.
        var match = TrxIdPattern().Match(upper);
        return match.Success ? match.Value : null;
    }

    public static bool IsWellFormed(string? input) => Normalise(input) is not null;
}
