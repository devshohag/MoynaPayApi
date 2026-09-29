using System.Security.Cryptography;
using System.Text;
using MoynaPay.Application.Abstractions;
using MoynaPay.Domain.Payments;

namespace MoynaPay.Application.AppDevices;

public sealed class AppDevicesService(IAppDeviceStore devices, IClock clock)
{
    public static readonly TimeSpan PairingTtl = TimeSpan.FromMinutes(5);

    public async Task<CreatePairingTokenResult> CreatePairingTokenAsync(
        Guid merchantId, CancellationToken ct = default)
    {
        var now = clock.UtcNow;
        var token = NewToken();
        var row = new AppDevicePairingToken
        {
            Id = Guid.CreateVersion7(),
            MerchantId = merchantId,
            TokenHash = Hash(token),
            ExpiresAt = now.Add(PairingTtl),
            CreatedAt = now,
        };

        await devices.SavePairingTokenAsync(row, ct).ConfigureAwait(false);

        return new CreatePairingTokenResult(token, row.ExpiresAt);
    }

    public async Task<PairDeviceResult> PairAsync(PairDeviceCommand command,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        var tokenHash = HashInput(command.PairingToken);
        var fingerprint = Clean(command.Fingerprint, 160);
        if (tokenHash is null || fingerprint is null)
        {
            return new PairDeviceResult(PairDeviceOutcome.Invalid, null, null,
                "Pairing token and device fingerprint are required.");
        }

        var now = clock.UtcNow;
        var pairing = await devices.FindPairingTokenAsync(tokenHash, ct).ConfigureAwait(false);
        if (pairing is null || !pairing.IsUsable(now))
        {
            return new PairDeviceResult(PairDeviceOutcome.Invalid, null, null,
                "The pairing token is invalid or expired.");
        }

        var deviceToken = NewToken();
        var device = new AppDevice
        {
            Id = Guid.CreateVersion7(),
            MerchantId = pairing.MerchantId,
            DeviceTokenHash = Hash(deviceToken),
            Fingerprint = fingerprint,
            Name = Clean(command.Name, 120),
            Model = Clean(command.Model, 120),
            AppVersion = Clean(command.AppVersion, 40),
            PushToken = Clean(command.PushToken, 500),
            PermissionState = command.PermissionState ?? DevicePermissionState.Unknown,
            BatteryPercent = ClampBattery(command.BatteryPercent),
            NetworkType = Clean(command.NetworkType, 40),
            LastHeartbeatAt = now,
            CreatedAt = now,
            UpdatedAt = now,
        };

        await devices.SaveDeviceAsync(device, ct).ConfigureAwait(false);
        await devices.ConsumePairingTokenAsync(pairing, device.Id, now, ct).ConfigureAwait(false);

        return new PairDeviceResult(PairDeviceOutcome.Paired, DeviceView.From(device), deviceToken, null);
    }

    public async Task<DeviceUpdateResult> HeartbeatAsync(
        Guid deviceId, string? deviceToken, DeviceHeartbeatCommand command,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        var device = await AuthenticatedDeviceAsync(deviceId, deviceToken, ct).ConfigureAwait(false);
        if (device is null)
        {
            return new DeviceUpdateResult(DeviceUpdateOutcome.Unauthorized, null,
                "Invalid device credentials.");
        }

        var now = clock.UtcNow;
        device.LastHeartbeatAt = now;
        device.PermissionState = command.PermissionState ?? device.PermissionState;
        device.BatteryPercent = ClampBattery(command.BatteryPercent);
        device.NetworkType = Clean(command.NetworkType, 40);
        device.AppVersion = Clean(command.AppVersion, 40) ?? device.AppVersion;
        device.Model = Clean(command.Model, 120) ?? device.Model;
        device.UpdatedAt = now;

        await devices.SaveDeviceAsync(device, ct).ConfigureAwait(false);

        return new DeviceUpdateResult(DeviceUpdateOutcome.Updated, DeviceView.From(device), null);
    }

    public async Task<DeviceUpdateResult> UpdatePushTokenAsync(
        Guid deviceId, string? deviceToken, string? pushToken, CancellationToken ct = default)
    {
        var cleaned = Clean(pushToken, 500);
        if (cleaned is null)
        {
            return new DeviceUpdateResult(DeviceUpdateOutcome.Invalid, null,
                "Push token is required.");
        }

        var device = await AuthenticatedDeviceAsync(deviceId, deviceToken, ct).ConfigureAwait(false);
        if (device is null)
        {
            return new DeviceUpdateResult(DeviceUpdateOutcome.Unauthorized, null,
                "Invalid device credentials.");
        }

        device.PushToken = cleaned;
        device.UpdatedAt = clock.UtcNow;
        await devices.SaveDeviceAsync(device, ct).ConfigureAwait(false);

        return new DeviceUpdateResult(DeviceUpdateOutcome.Updated, DeviceView.From(device), null);
    }

    public async Task<IReadOnlyList<DeviceView>> ListAsync(Guid merchantId,
        CancellationToken ct = default)
    {
        var rows = await devices.ListDevicesAsync(merchantId, ct).ConfigureAwait(false);

        return rows.Select(DeviceView.From).ToList();
    }

    private async Task<AppDevice?> AuthenticatedDeviceAsync(
        Guid deviceId, string? deviceToken, CancellationToken ct)
    {
        var hash = HashInput(deviceToken);
        return hash is null
            ? null
            : await devices.FindByCredentialAsync(deviceId, hash, ct).ConfigureAwait(false);
    }

    private static int? ClampBattery(int? value) =>
        value is null ? null : Math.Clamp(value.Value, 0, 100);

    private static string? Clean(string? value, int max)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;

        var trimmed = value.Trim();
        return trimmed.Length <= max ? trimmed : trimmed[..max];
    }

    private static string? HashInput(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : Hash(value.Trim());

    private static string NewToken()
    {
        Span<byte> bytes = stackalloc byte[32];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToBase64String(bytes)
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');
    }

    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}

public sealed record CreatePairingTokenResult(string Token, DateTimeOffset ExpiresAt);

public sealed record PairDeviceCommand(
    string? PairingToken,
    string? Fingerprint,
    string? Name,
    string? Model,
    string? AppVersion,
    string? PushToken,
    DevicePermissionState? PermissionState,
    int? BatteryPercent,
    string? NetworkType);

public enum PairDeviceOutcome
{
    Paired,
    Invalid,
}

public sealed record PairDeviceResult(
    PairDeviceOutcome Outcome,
    DeviceView? Device,
    string? DeviceToken,
    string? Reason);

public sealed record DeviceHeartbeatCommand(
    DevicePermissionState? PermissionState,
    int? BatteryPercent,
    string? NetworkType,
    string? AppVersion,
    string? Model);

public enum DeviceUpdateOutcome
{
    Updated,
    Invalid,
    Unauthorized,
}

public sealed record DeviceUpdateResult(
    DeviceUpdateOutcome Outcome,
    DeviceView? Device,
    string? Reason);

public sealed record DeviceView(
    Guid Id,
    string Fingerprint,
    string? Name,
    string? Model,
    string? AppVersion,
    bool IsActive,
    bool HasPushToken,
    DateTimeOffset? LastHeartbeatAt,
    DevicePermissionState PermissionState,
    int? BatteryPercent,
    string? NetworkType,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt)
{
    public static DeviceView From(AppDevice device) => new(
        device.Id,
        device.Fingerprint,
        device.Name,
        device.Model,
        device.AppVersion,
        device.IsActive,
        !string.IsNullOrWhiteSpace(device.PushToken),
        device.LastHeartbeatAt,
        device.PermissionState,
        device.BatteryPercent,
        device.NetworkType,
        device.CreatedAt,
        device.UpdatedAt);
}
