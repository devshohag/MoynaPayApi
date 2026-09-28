using MoynaPay.Domain.Common;
using MoynaPay.Domain.Payments;

namespace MoynaPay.Domain.Payments;

/// <summary>
/// A message pattern, stored as data rather than code.
///
/// Operators change their wording without notice. Keeping patterns in the database
/// means a fix ships as a row, not a deployment, which is the difference between
/// minutes and hours of merchants not getting paid.
/// </summary>
public class ParserTemplate : BaseEntity
{
    public PaymentMethod Method { get; set; }
    public string SenderId { get; set; } = null!;

    /// <summary>.NET regular expression with named groups.</summary>
    public string Pattern { get; set; } = null!;

    /// <summary>Maps named groups to fields, e.g. {"amount":"amt","trxId":"trx"}.</summary>
    public string FieldMapJson { get; set; } = null!;

    /// <summary>
    /// False for the patterns that exist purely to recognise and discard non-payments
    /// - cash out, recharge, promotions, OTP. Negative templates matter as much as
    /// positive ones.
    /// </summary>
    public bool IsCredit { get; set; } = true;

    public int Version { get; set; } = 1;
    public bool IsActive { get; set; } = true;
    public decimal BaseConfidence { get; set; } = 1.00m;

    public long HitCount { get; set; }
    public DateTimeOffset? LastHitAt { get; set; }
    public string? Description { get; set; }
}
