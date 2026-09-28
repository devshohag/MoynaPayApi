using MoynaPay.Domain.Common;

namespace MoynaPay.Domain.Payments;

/// <summary>
/// A one-shot code that lets a phone join a wallet.
///
/// Short lived and single use because it travels the least trustworthy path in the whole
/// system: it is displayed as a QR code on a dashboard, in an office, possibly on a shared
/// screen, and photographed by a camera. Five minutes and one use means a photograph of it
/// is worth nothing by the time anyone thinks to try.
///
/// Only the hash is stored. The token itself exists in the QR code and nowhere else.
/// </summary>
public class DevicePairingToken : BaseEntity
{
    public Guid WalletId { get; set; }

    /// <summary>SHA-256 of the token. The token is never written down.</summary>
    public string TokenHash { get; set; } = null!;

    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? ConsumedAt { get; set; }
    public Guid? DeviceId { get; set; }

    public bool IsUsable(DateTimeOffset asOf) => ConsumedAt is null && asOf <= ExpiresAt;
}
