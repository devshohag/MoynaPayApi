using MoynaPay.Domain.Common;

namespace MoynaPay.Domain.Merchants;

// Schema: merchants

/// <summary>
/// One attempt against one rate-limited key, at one moment.
///
/// A row per attempt rather than a counter per key, because the limit is a sliding window
/// - "three OTP requests in ten minutes" - and a counter cannot answer that without also
/// storing when it was last reset, which is the same information written less honestly.
///
/// Held in memory this was a list per key in a dictionary that nothing ever emptied: an
/// attacker walking through valid-looking phone numbers added a key each time and none of
/// them was ever collected. Here the old rows are deleted by the same statement that
/// counts the new ones.
///
/// TenantId is Guid.Empty for keys that belong to nobody yet - an OTP request names a
/// phone number, and whether a merchant owns it is exactly what has not been established.
/// </summary>
public class RateLimitHit : BaseEntity
{
    /// <summary>The bucket, e.g. "app:otp:8801711223344".</summary>
    public string Key { get; set; } = default!;

    public DateTimeOffset At { get; set; } = DateTimeOffset.UtcNow;
}
