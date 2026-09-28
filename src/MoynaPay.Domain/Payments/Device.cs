using MoynaPay.Domain.Common;
using MoynaPay.Domain.Payments;

namespace MoynaPay.Domain.Payments;

/// <summary>
/// An Android phone paired to a wallet, reporting the notifications and messages that
/// prove a payment arrived. The most fragile component in the product, so its health is
/// first-class data rather than a log line.
///
/// Priority orders failover: 1 is primary, 2 takes over when the primary goes quiet.
///
/// Pairing and request authentication use a keypair generated on the device, not client
/// certificates. Full mTLS through a terminating proxy needs a CA, issuance, rotation,
/// revocation and a lost-phone recovery path - months of work for a single developer,
/// and barely stronger than a signed request from a key that never leaves the handset.
/// mTLS stays on the roadmap; it is not an MVP requirement.
/// </summary>
public class Device : BaseEntity
{
    public Guid WalletId { get; set; }

    /// <summary>Stable per-install identifier issued at pairing.</summary>
    public string Fingerprint { get; set; } = null!;

    /// <summary>Base64 public key generated on the device. The private half never leaves it.</summary>
    public string? PublicKey { get; set; }

    public string? Model { get; set; }
    public string? AppVersion { get; set; }
    public int Priority { get; set; } = 1;
    public bool IsActive { get; set; } = true;

    public DateTimeOffset? LastHeartbeatAt { get; set; }
    public DevicePermissionState PermissionState { get; set; } = DevicePermissionState.Unknown;
    public int? BatteryPercent { get; set; }
    public string? NetworkType { get; set; }

    public Wallet Wallet { get; set; } = null!;

    /// <summary>
    /// Whether the phone has gone quiet. The threshold is a policy decision that depends
    /// on whether a payment is in flight, so it is passed in rather than guessed at here
    /// - see HeartbeatPolicy in the application layer.
    /// </summary>
    public bool IsSilent(DateTimeOffset asOf, TimeSpan threshold) =>
        LastHeartbeatAt is null || asOf - LastHeartbeatAt.Value > threshold;
}
