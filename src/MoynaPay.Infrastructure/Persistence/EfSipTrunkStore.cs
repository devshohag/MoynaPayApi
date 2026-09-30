using Microsoft.EntityFrameworkCore;
using MoynaPay.Application.Abstractions;
using MoynaPay.Domain.Voice;

namespace MoynaPay.Infrastructure.Persistence;

public sealed class EfSipTrunkStore(MoynaPayDbContext db) : ISipTrunkStore
{
    public Task<SipTrunk?> FindActiveForMerchantAsync(Guid merchantId, CancellationToken ct = default) =>
        db.SipTrunks.AsNoTracking()
            .Where(t => t.TenantId == merchantId && t.IsActive)
            .OrderByDescending(t => t.UpdatedAt)
            .FirstOrDefaultAsync(ct);

    public async Task SaveAsync(SipTrunk trunk, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(trunk);

        db.SipTrunks.Update(trunk);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }
}
