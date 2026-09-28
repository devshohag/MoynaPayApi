namespace MoynaPay.Application.Payments.Invoicing;

/// <summary>
/// Picks the exact figure a customer is asked to send, so that no two open sessions on
/// one wallet expect the same amount.
///
/// The salt goes upward, never downward. Either direction makes the figure unambiguous,
/// but a downward salt quietly costs the merchant money on every order, and "we charged
/// you two taka less" is a conversation nobody wants to have with their accountant. The
/// customer is shown the charged figure and nothing else, so there is no surprise either
/// way.
///
/// The ceiling scales with the invoice: five taka on top of a five thousand taka order is
/// noise, five taka on a fifty taka order is a ten percent surcharge. That deliberately
/// leaves small invoices with very few distinct slots - a fifty taka invoice gets two -
/// which is a real capacity limit, not an oversight. When the slots run out the answer is
/// to fall back to the transaction id flow, not to widen the salt until the number stops
/// resembling the price.
/// </summary>
public static class AmountAllocator
{
    public const decimal AbsoluteMaxSaltBdt = 5m;

    /// <summary>Two percent of the invoice, at least one taka, at most five.</summary>
    public static decimal MaxSaltFor(decimal amount)
    {
        var proportional = decimal.Round(amount * 0.02m, 0, MidpointRounding.ToZero);
        return Math.Clamp(proportional, 1m, AbsoluteMaxSaltBdt);
    }

    /// <summary>
    /// Returns the amount to charge, or null when every slot in the window is taken. Null
    /// is a normal outcome on a busy wallet with small invoices, and the caller answers it
    /// by switching that invoice to <see cref="MatchingMode.TrxId"/>.
    /// </summary>
    public static decimal? Allocate(decimal amount, IReadOnlyCollection<decimal> takenAmounts)
    {
        ArgumentNullException.ThrowIfNull(takenAmounts);

        if (amount <= 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(amount), "Amount must be positive.");
        }

        var taken = new HashSet<decimal>(takenAmounts);
        var maxSalt = MaxSaltFor(amount);

        for (var salt = 0m; salt <= maxSalt; salt++)
        {
            var candidate = amount + salt;
            if (!taken.Contains(candidate))
            {
                return candidate;
            }
        }

        return null;
    }
}
