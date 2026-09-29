using Microsoft.EntityFrameworkCore;
using MoynaPay.Application.Abstractions;

namespace MoynaPay.Infrastructure.Persistence;

public sealed class EfAppDeviceStore(MoynaPayDbContext db) : IAppDeviceStore
{
    public async Task SavePairingTokenAsync(AppDevicePairingToken token,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(token);

        db.AppDevicePairingTokens.Add(token);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public Task<AppDevicePairingToken?> FindPairingTokenAsync(string tokenHash,
        CancellationToken ct = default) =>
        db.AppDevicePairingTokens.FirstOrDefaultAsync(t => t.TokenHash == tokenHash, ct);

    public async Task ConsumePairingTokenAsync(AppDevicePairingToken token, Guid deviceId,
        DateTimeOffset now, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(token);

        token.ConsumedAt = now;
        token.DeviceId = deviceId;
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task SaveDeviceAsync(AppDevice device, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(device);

        var tracked = db.ChangeTracker.Entries<AppDevice>()
            .Any(e => ReferenceEquals(e.Entity, device));

        if (!tracked)
        {
            var exists = await db.AppDevices.IgnoreQueryFilters()
                .AnyAsync(d => d.Id == device.Id, ct)
                .ConfigureAwait(false);

            if (exists) db.AppDevices.Update(device);
            else db.AppDevices.Add(device);
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public Task<AppDevice?> FindDeviceAsync(Guid merchantId, Guid deviceId,
        CancellationToken ct = default) =>
        db.AppDevices.FirstOrDefaultAsync(d =>
            d.MerchantId == merchantId && d.Id == deviceId && d.IsActive, ct);

    public Task<AppDevice?> FindByCredentialAsync(Guid deviceId, string deviceTokenHash,
        CancellationToken ct = default) =>
        db.AppDevices.IgnoreQueryFilters().FirstOrDefaultAsync(d =>
            d.Id == deviceId
            && d.DeviceTokenHash == deviceTokenHash
            && d.IsActive, ct);

    public async Task<IReadOnlyList<AppDevice>> ListDevicesAsync(Guid merchantId,
        CancellationToken ct = default) =>
        await db.AppDevices
            .Where(d => d.MerchantId == merchantId && d.IsActive)
            .OrderByDescending(d => d.LastHeartbeatAt ?? d.CreatedAt)
            .ToListAsync(ct)
            .ConfigureAwait(false);
}
