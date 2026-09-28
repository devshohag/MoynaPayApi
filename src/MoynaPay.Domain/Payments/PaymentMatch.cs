using MoynaPay.Domain.Common;
using MoynaPay.Domain.Payments;

namespace MoynaPay.Domain.Payments;

/// <summary>
/// The link between money that arrived and an invoice that was waiting for it.
///
/// InvoiceId and ParsedTransactionId each carry a unique index. Those two constraints
/// are the final authority on correctness: whatever happens upstream - a retried queue
/// message, a duplicated upload, two workers racing - the database permits exactly one
/// match per invoice and per transaction.
/// </summary>
public class PaymentMatch : BaseEntity
{
    public Guid InvoiceId { get; set; }
    public Guid ParsedTransactionId { get; set; }
    public Guid? PaymentSessionId { get; set; }

    public MatchStrategy Strategy { get; set; }
    public DateTimeOffset MatchedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Set only for MatchStrategy.Manual, and always with an audit log entry.</summary>
    public string? OperatorId { get; set; }

    public Invoice Invoice { get; set; } = null!;
    public ParsedTransaction ParsedTransaction { get; set; } = null!;
}
