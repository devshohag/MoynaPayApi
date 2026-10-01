using Microsoft.EntityFrameworkCore;
using MoynaPay.Application.Abstractions;
using MoynaPay.Domain.Payments;

namespace MoynaPay.Infrastructure.Persistence;

public sealed class EfRawEventStore(MoynaPayDbContext db) : IRawEventStore
{
    public async Task<bool> AddIfNewAsync(RawEvent rawEvent, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(rawEvent);

        db.RawEvents.Add(rawEvent);

        try
        {
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            return true;
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            db.Entry(rawEvent).State = EntityState.Detached;
            return false;
        }
    }

    public Task<RawEvent?> FindAsync(Guid merchantId, Guid rawEventId,
        CancellationToken ct = default) =>
        db.RawEvents.FirstOrDefaultAsync(e => e.TenantId == merchantId && e.Id == rawEventId, ct);

    public async Task<IReadOnlyList<RawEvent>> ClaimReceivedAsync(int take,
        CancellationToken ct = default)
    {
        var rows = await db.RawEvents
            .Where(e => e.State == RawEventState.Received)
            .OrderBy(e => e.DeviceReceivedAt)
            .Take(Math.Max(0, take))
            .ToListAsync(ct)
            .ConfigureAwait(false);

        foreach (var row in rows)
        {
            row.State = RawEventState.Claimed;
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        return rows;
    }

    public async Task SaveAsync(RawEvent rawEvent, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(rawEvent);

        if (db.Entry(rawEvent).State == EntityState.Detached)
        {
            db.RawEvents.Update(rawEvent);
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<RawEvent>> ListByMerchantAsync(Guid merchantId,
        CancellationToken ct = default) =>
        await db.RawEvents
            .Where(e => e.TenantId == merchantId)
            .OrderBy(e => e.DeviceReceivedAt)
            .ToListAsync(ct)
            .ConfigureAwait(false);

    private static bool IsUniqueViolation(DbUpdateException ex) =>
        ex.InnerException?.GetType().Name == "PostgresException"
        && string.Equals(
            ex.InnerException.GetType().GetProperty("SqlState")?.GetValue(ex.InnerException)?.ToString(),
            "23505",
            StringComparison.Ordinal);
}
