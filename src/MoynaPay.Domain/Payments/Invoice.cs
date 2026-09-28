using MoynaPay.Domain.Common;
using MoynaPay.Domain.Payments;

namespace MoynaPay.Domain.Payments;

/// <summary>
/// One payment request from a merchant.
///
/// Amount is what the merchant billed. ChargedAmount is what the customer is actually
/// told to send - the two differ only when the unique-amount strategy salts the figure,
/// and the checkout page always shows ChargedAmount so nobody is surprised at
/// reconciliation time.
/// </summary>
public class Invoice : BaseEntity
{
    public Guid WalletId { get; set; }

    /// <summary>The merchant's own order identifier. Unique per merchant.</summary>
    public string OrderRef { get; set; } = null!;

    public decimal Amount { get; set; }
    public decimal ChargedAmount { get; set; }
    public string Currency { get; set; } = "BDT";

    public InvoiceStatus Status { get; set; } = InvoiceStatus.Created;

    public DateTimeOffset ExpiresAt { get; set; }

    /// <summary>
    /// Grace beyond ExpiresAt during which a late payment still matches. Operator
    /// messages run minutes behind and an offline device uploads hours late, so an
    /// invoice moves to PendingReview here rather than failing.
    /// </summary>
    public DateTimeOffset GraceUntil { get; set; }

    public DateTimeOffset? PaidAt { get; set; }

    /// <summary>
    /// Which flow this invoice is on, decided when it was created and fixed afterwards.
    ///
    /// Stored rather than recomputed because two different things read it at two different
    /// times: the checkout page, which must show a transaction id box or not, and the
    /// matcher, which must know whether an unclaimed payment of exactly this amount is
    /// allowed to settle it. Recomputing from the wallet's current state would let an
    /// invoice quietly change flows underneath a customer who already has the page open.
    /// </summary>
    public MatchingMode Mode { get; set; } = MatchingMode.TrxId;

    public string? CustomerName { get; set; }
    public string? CustomerEmail { get; set; }
    public string? CustomerMsisdn { get; set; }

    public string? RedirectUrl { get; set; }
    public string? CallbackUrl { get; set; }

    /// <summary>Merchant-supplied JSON echoed back on the webhook. Opaque to MoynaPay.</summary>
    public string? MetadataJson { get; set; }

    public Wallet Wallet { get; set; } = null!;
    public ICollection<PaymentSession> Sessions { get; set; } = new List<PaymentSession>();
}
