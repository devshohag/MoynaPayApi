namespace MoynaPay.Domain.Payments;

/// <summary>
/// How a payment for this invoice will be recognised.
///
/// TrxId asks the customer to copy the transaction id from their bKash app onto the
/// checkout page. It costs a step and buys something worth more early on: a payment can
/// only settle the invoice whose id the customer actually pasted, so a wrong match is
/// close to impossible. One wrong match costs more trust than a hundred slower checkouts.
///
/// UniqueAmount asks the customer for nothing. The invoice reserves one exact figure on
/// the wallet - 500.13 rather than 500.00 - and the matcher recognises the payment by that
/// figure alone. It is the experience worth having, and it rests on the amount being
/// unique among the open sessions on that wallet, which the partial unique index enforces
/// and AmountAllocator works within. When every figure inside the salt ceiling is already
/// taken, the invoice falls back to TrxId rather than distorting the price.
///
/// Lives in the domain rather than the application layer because the invoice carries it:
/// the checkout page has to know which of the two it is showing, and so does the matcher.
/// </summary>
public enum MatchingMode
{
    TrxId = 0,
    UniqueAmount = 1,
}
