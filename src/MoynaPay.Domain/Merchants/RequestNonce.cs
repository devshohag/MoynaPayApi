using MoynaPay.Domain.Common;

namespace MoynaPay.Domain.Merchants;

// Schema: merchants

/// <summary>
/// A signature that has been used once and may not be used again.
///
/// The timestamp window alone does not stop replay: inside those five minutes the same
/// signed request can be sent again and again, and every copy verifies, because the MAC is
/// over bytes that have not changed. This is the row that makes the second one fail.
///
/// In memory it was enough while there was one API. With two behind a load balancer, a
/// captured request replayed against the other instance is accepted, because that instance
/// never saw the first one. It has to be shared state, which means it has to be a table.
///
/// TenantId is present because everything in this schema carries one, but it is not what
/// identifies a nonce: the key id is. A nonce is refused before the merchant behind the
/// key has been established.
/// </summary>
public class RequestNonce : BaseEntity
{
    public string KeyId { get; set; } = default!;

    public string Nonce { get; set; } = default!;

    public DateTimeOffset SeenAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// When this row stops being worth keeping. Anything older than the signature
    /// tolerance is already refused by the clock, so holding it buys no safety and costs a
    /// table that grows forever.
    /// </summary>
    public DateTimeOffset ExpiresAt { get; set; }
}
