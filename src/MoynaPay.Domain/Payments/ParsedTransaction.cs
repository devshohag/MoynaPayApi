using MoynaPay.Domain.Common;
using MoynaPay.Domain.Payments;

namespace MoynaPay.Domain.Payments;

/// <summary>
/// The structured reading of one raw event. Confidence below the parser threshold goes
/// to the ops queue instead of the matcher - a cashback or promotional message must
/// never be read as a payment.
/// </summary>
public class ParsedTransaction : BaseEntity
{
    public Guid RawEventId { get; set; }
    public Guid WalletId { get; set; }

    public PaymentMethod Method { get; set; }
    public decimal Amount { get; set; }

    /// <summary>Provider transaction id. Unique per method across the whole system.</summary>
    public string TrxId { get; set; } = null!;

    public string? SenderMsisdn { get; set; }
    public decimal? BalanceAfter { get; set; }

    /// <summary>When the provider says the payment happened, as printed in the message.</summary>
    public DateTimeOffset OccurredAt { get; set; }

    public Guid? TemplateId { get; set; }

    /// <summary>0.00 to 1.00. Below 0.80 the matcher does not act on it.</summary>
    public decimal Confidence { get; set; }

    public RawEvent RawEvent { get; set; } = null!;
    public ParserTemplate? Template { get; set; }
}
