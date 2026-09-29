using Microsoft.EntityFrameworkCore;
using MoynaPay.Application.Abstractions;
using MoynaPay.Domain.Merchants;

namespace MoynaPay.Infrastructure.Persistence;

/// <summary>
/// Merchants, their subscription, their signing keys and their webhook, against Postgres.
///
/// Every method still takes a merchant id even though a global query filter is in place.
/// Belt and braces on purpose: the filter protects a query somebody forgot to scope, and
/// the parameter protects against a context whose merchant was never set - a worker, say,
/// reaching into one merchant's rows.
/// </summary>
public sealed class EfMerchantStore(MoynaPayDbContext db) : IMerchantStore
{
    public Task<Merchant?> FindAsync(Guid merchantId, CancellationToken ct = default) =>
        db.Merchants.FirstOrDefaultAsync(m => m.Id == merchantId, ct);

    public Task<Merchant?> FindByMsisdnAsync(string msisdn, CancellationToken ct = default) =>
        // Deliberately outside the tenant filter: this is how the app login finds out which
        // merchant is calling, so the merchant is exactly what is not known yet.
        db.Merchants.IgnoreQueryFilters()
            .FirstOrDefaultAsync(m => m.Msisdn == msisdn && !m.IsDeleted, ct);

    public async Task SaveMerchantAsync(Merchant merchant, Subscription subscription,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(merchant);
        ArgumentNullException.ThrowIfNull(subscription);

        Track(merchant);
        Track(subscription);

        try
        {
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        catch (DbUpdateException ex) when (Postgres.IsUniqueViolation(ex))
        {
            // Two people onboarding the same shop at once. The unique index on msisdn is
            // what decides it; the loser is told, rather than creating a second merchant on
            // a phone number that is somebody's login.
            db.ChangeTracker.Clear();
            throw new InvalidOperationException("duplicate merchant", ex);
        }
    }

    public async Task SaveMerchantProfileAsync(Merchant merchant, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(merchant);

        Track(merchant);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task<Subscription> SubscriptionAsync(Guid merchantId, CancellationToken ct = default)
    {
        var found = await db.Subscriptions
            .FirstOrDefaultAsync(s => s.TenantId == merchantId, ct).ConfigureAwait(false);

        // A merchant with no row has bought nothing. An empty subscription keeps the
        // pipeline's answer defined - skip every step - rather than throwing somewhere deep
        // in a worker at two in the morning.
        return found ?? new Subscription { TenantId = merchantId };
    }

    /// <summary>
    /// Resolves a signing key before any merchant is known, so this one query is
    /// deliberately outside the tenant filter. It and FindByMsisdnAsync are the only two.
    /// </summary>
    public Task<ApiCredential?> FindCredentialAsync(string keyId, CancellationToken ct = default) =>
        db.ApiCredentials.IgnoreQueryFilters()
            .FirstOrDefaultAsync(c => c.KeyId == keyId && !c.IsDeleted && c.RevokedAt == null, ct);

    public async Task<IReadOnlyList<ApiCredential>> ListCredentialsAsync(
        Guid merchantId, CancellationToken ct = default) =>
        await db.ApiCredentials.AsNoTracking()
            .Where(c => c.TenantId == merchantId)
            .OrderByDescending(c => c.CreatedAt)
            .ToListAsync(ct)
            .ConfigureAwait(false);

    public Task<bool> HasAnyCredentialAsync(Guid merchantId, CancellationToken ct = default) =>
        db.ApiCredentials.AnyAsync(c => c.TenantId == merchantId, ct);

    public async Task SaveCredentialAsync(ApiCredential credential, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(credential);

        Track(credential);

        try
        {
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        catch (DbUpdateException ex) when (Postgres.IsUniqueViolation(ex))
        {
            db.ChangeTracker.Clear();
            throw new InvalidOperationException("duplicate key", ex);
        }
    }

    public async Task<bool> RevokeCredentialAsync(Guid merchantId, string keyId, DateTimeOffset at,
        CancellationToken ct = default)
    {
        var credential = await db.ApiCredentials
            .FirstOrDefaultAsync(c => c.KeyId == keyId && c.TenantId == merchantId, ct)
            .ConfigureAwait(false);

        if (credential is null || credential.RevokedAt is not null) return false;

        credential.RevokedAt = at;

        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        return true;
    }

    public Task<WebhookEndpoint?> WebhookAsync(Guid merchantId, CancellationToken ct = default) =>
        db.WebhookEndpoints.FirstOrDefaultAsync(w => w.TenantId == merchantId, ct);

    public async Task SaveWebhookAsync(WebhookEndpoint endpoint, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(endpoint);

        Track(endpoint);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task UpdateWebhookDeliveryAsync(Guid merchantId, DateTimeOffset? deliveredAt,
        string? failureReason, CancellationToken ct = default)
    {
        var endpoint = await db.WebhookEndpoints
            .FirstOrDefaultAsync(w => w.TenantId == merchantId, ct).ConfigureAwait(false);

        if (endpoint is null) return;

        endpoint.LastDeliveredAt = deliveredAt ?? endpoint.LastDeliveredAt;
        endpoint.LastFailureReason = failureReason;

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Adds the entity if this context has never seen it, and otherwise leaves it alone.
    ///
    /// The services above hand back either a row they read through one of these methods -
    /// already tracked, so SaveChanges writes an UPDATE - or one they just constructed,
    /// which has to be inserted. Calling Add on a tracked entity would try to insert it
    /// again.
    /// </summary>
    private void Track<T>(T entity) where T : class
    {
        if (db.Entry(entity).State == EntityState.Detached) db.Add(entity);
    }
}

/// <summary>
/// The two Postgres error codes this layer has an opinion about, matched on the code
/// rather than the message - the message is localised and has changed between server
/// versions.
/// </summary>
internal static class Postgres
{
    public const string UniqueViolation = "23505";

    public static bool IsUniqueViolation(DbUpdateException ex) =>
        ex.InnerException is Npgsql.PostgresException { SqlState: UniqueViolation };
}
