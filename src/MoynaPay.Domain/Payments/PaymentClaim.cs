using MoynaPay.Domain.Common;
using MoynaPay.Domain.Payments;

namespace MoynaPay.Domain.Payments;

/// <summary>
/// A customer saying "I paid, here is the transaction id".
///
/// A claim is an assertion, not a fact, and the distinction is the whole reason this is
/// its own table rather than a column on the invoice. The customer may mistype, may paste
/// the wrong message, may try again a minute later; all of those attempts are worth
/// keeping, because when a payment goes missing the first question is always what the
/// customer actually typed.
///
/// Nothing here settles anything. The matcher decides, by finding a parsed transaction
/// with this id that arrived on the right wallet in the right window.
/// </summary>
public class PaymentClaim : BaseEntity
{
    public Guid InvoiceId { get; set; }

    /// <summary>Exactly what the customer typed, kept for support.</summary>
    public string SubmittedText { get; set; } = null!;

    /// <summary>Upper-cased, stripped of spaces. What the matcher compares.</summary>
    public string NormalisedTrxId { get; set; } = null!;

    public ClaimState State { get; set; } = ClaimState.Pending;

    public DateTimeOffset SubmittedAt { get; set; } = DateTimeOffset.UtcNow;
    public string? ClientIp { get; set; }

    public Invoice Invoice { get; set; } = null!;
}
