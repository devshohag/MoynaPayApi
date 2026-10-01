using Microsoft.EntityFrameworkCore;
using MoynaPay.Application.Abstractions;
using MoynaPay.Application.Payments.Invoicing;
using MoynaPay.Application.Security;
using MoynaPay.Domain.Merchants;
using MoynaPay.Domain.Payments;

namespace MoynaPay.Infrastructure.Persistence;

public sealed class EfInvoiceStore(MoynaPayDbContext db) : IInvoiceStore
{
    public Task<Invoice?> FindByOrderRefAsync(Guid merchantId, string orderRef,
        CancellationToken ct = default) =>
        db.Invoices.FirstOrDefaultAsync(i => i.TenantId == merchantId && i.OrderRef == orderRef, ct);

    public async Task<IReadOnlyList<decimal>> ListOpenChargedAmountsAsync(Guid merchantId,
        DateTimeOffset at, CancellationToken ct = default) =>
        await db.Invoices
            .Where(i => i.TenantId == merchantId
                && !i.IsDeleted
                && (i.Status == InvoiceStatus.Created || i.Status == InvoiceStatus.AwaitingPayment)
                && i.GraceUntil >= at)
            .Select(i => i.ChargedAmount)
            .ToListAsync(ct)
            .ConfigureAwait(false);

    public async Task<IReadOnlyList<Invoice>> FindOpenByChargedAmountAsync(Guid merchantId,
        decimal chargedAmount, DateTimeOffset occurredAt, CancellationToken ct = default)
    {
        var candidates = await db.Invoices
            .Where(i => i.TenantId == merchantId
                && !i.IsDeleted
                && i.Status == InvoiceStatus.AwaitingPayment
                && i.Mode == MatchingMode.UniqueAmount
                && i.ChargedAmount == chargedAmount)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return candidates.Where(i => InvoiceWindow.Default.Accepts(i, occurredAt)).ToList();
    }

    public Task<bool> HasMatchedTransactionAsync(PaymentMethod method, string trxId,
        CancellationToken ct = default) =>
        (from match in db.PaymentMatches
         join transaction in db.ParsedTransactions on match.ParsedTransactionId equals transaction.Id
         where transaction.Method == method && transaction.TrxId == trxId
         select match.Id).AnyAsync(ct);

    public async Task SaveMatchAsync(Invoice invoice, ParsedTransaction transaction,
        MatchStrategy strategy, DateTimeOffset matchedAt, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(invoice);
        ArgumentNullException.ThrowIfNull(transaction);

        if (db.Entry(invoice).State == EntityState.Detached) db.Invoices.Update(invoice);
        db.ParsedTransactions.Add(transaction);
        db.PaymentMatches.Add(new PaymentMatch
        {
            Id = Guid.CreateVersion7(),
            TenantId = invoice.TenantId,
            InvoiceId = invoice.Id,
            ParsedTransactionId = transaction.Id,
            Strategy = strategy,
            MatchedAt = matchedAt,
            CreatedAt = matchedAt,
            UpdatedAt = matchedAt,
        });

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task SaveAsync(Invoice invoice, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(invoice);

        if (db.Entry(invoice).State == EntityState.Detached) db.Invoices.Add(invoice);

        try
        {
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        catch (DbUpdateException ex) when (Postgres.IsUniqueViolation(ex))
        {
            // One invoice per order reference. Two handlers running the same action at once
            // is exactly what the unique index is there to settle.
            db.ChangeTracker.Clear();
            throw new InvalidOperationException("duplicate invoice", ex);
        }
    }
}

/// <summary>
/// OTP challenges and app tokens.
///
/// Only hashes are stored, so nothing here can be read out and used. That is a property of
/// what the service writes, not of this store - but it is the reason none of these methods
/// takes or returns a code.
/// </summary>
public sealed class EfAppAuthStore(MoynaPayDbContext db) : IAppAuthStore
{
    public async Task SaveOtpAsync(AppOtpChallenge challenge, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(challenge);

        if (db.Entry(challenge).State == EntityState.Detached) db.AppOtpChallenges.Add(challenge);

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public Task<AppOtpChallenge?> LatestOtpAsync(string msisdn, CancellationToken ct = default) =>
        // Outside the tenant filter: an OTP request names a phone number, and which merchant
        // owns it is precisely what has not been established yet.
        db.AppOtpChallenges.IgnoreQueryFilters()
            .Where(o => o.Msisdn == msisdn)
            .OrderByDescending(o => o.CreatedAt)
            .FirstOrDefaultAsync(ct);

    public async Task SaveTokenAsync(AppToken token, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(token);

        if (db.Entry(token).State == EntityState.Detached) db.AppTokens.Add(token);

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public Task<AppToken?> FindTokenAsync(string tokenHash, AppTokenKind kind,
        CancellationToken ct = default) =>
        // A token is presented in order to find out who is calling, so this too runs before
        // the merchant is known.
        db.AppTokens.IgnoreQueryFilters()
            .FirstOrDefaultAsync(t => t.TokenHash == tokenHash && t.Kind == kind, ct);

    public async Task RevokeTokenAsync(string tokenHash, AppTokenKind kind, DateTimeOffset now,
        string? replacedByHash = null, CancellationToken ct = default)
    {
        // One statement, and it only fires on a token that is not already revoked - so a
        // refresh token presented twice at the same instant revokes once, and the second
        // caller can be told it lost.
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"""
             UPDATE merchants.app_tokens
             SET revoked_at = {now}, replaced_by_hash = {replacedByHash}
             WHERE token_hash = {tokenHash}
               AND kind = {kind.ToString()}
               AND revoked_at IS NULL
             """, ct).ConfigureAwait(false);

        db.ChangeTracker.Clear();
    }
}

/// <summary>
/// The sliding window, as rows.
///
/// A row per attempt rather than a counter, because "three in ten minutes" is a question
/// about when, and a counter has to be told when to reset - which is the same information
/// stored less honestly.
/// </summary>
public sealed class EfRateLimitStore(MoynaPayDbContext db) : IRateLimitStore
{
    public async Task<bool> TryConsumeAsync(string key, DateTimeOffset now, TimeSpan window,
        int limit, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        if (limit <= 0) return false;

        var cutoff = now.Subtract(window);
        var id = Guid.CreateVersion7();

        // Insert only if the window is not already full, in one statement - so the count and
        // the insert cannot be separated by another caller's insert. Under heavy contention
        // two transactions can still both read a count below the limit before either
        // commits, which lets a burst overshoot by the number of callers racing. For a limit
        // on OTP requests that is an acceptable edge; for anything that spends money it
        // would not be.
        var inserted = await db.Database.ExecuteSqlInterpolatedAsync(
            $"""
             INSERT INTO merchants.rate_limit_hits
                 (id, tenant_id, key, at, created_at, updated_at, is_deleted, row_version)
             SELECT {id}, {Guid.Empty}, {key}, {now}, {now}, {now}, false, 0
             WHERE (SELECT count(*) FROM merchants.rate_limit_hits
                    WHERE key = {key} AND at > {cutoff}) < {limit}
             """, ct).ConfigureAwait(false);

        if (inserted == 1)
        {
            // Swept here rather than by a background job, because this is the only code that
            // knows the window. Rows older than it can never count towards a limit again.
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"DELETE FROM merchants.rate_limit_hits WHERE key = {key} AND at <= {cutoff}",
                ct).ConfigureAwait(false);
        }

        return inserted == 1;
    }
}

public sealed class EfWorkflowSessionStore(MoynaPayDbContext db) : IWorkflowSessionStore
{
    public Task<WorkflowSession?> FindAsync(Guid merchantId, Guid orderId, string name,
        CancellationToken ct = default) =>
        db.WorkflowSessions.FirstOrDefaultAsync(
            s => s.MerchantId == merchantId && s.OrderId == orderId && s.Name == name, ct);

    public async Task SaveAsync(WorkflowSession session, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(session);

        if (db.Entry(session).State == EntityState.Detached) db.WorkflowSessions.Add(session);

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Takes one unfinished session for this runner, or returns null.
    ///
    /// SKIP LOCKED is what makes two runners useful rather than a queue: they take different
    /// sessions instead of waiting behind each other, and - the part that matters - never
    /// the same one, which would resume one order's workflow twice.
    /// </summary>
    public async Task<WorkflowSession?> TryLeaseNextAsync(string name, string runnerId,
        DateTimeOffset now, TimeSpan leaseFor, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(runnerId);

        var until = now.Add(leaseFor);

        var leased = await db.Database.ExecuteSqlInterpolatedAsync(
            $"""
             UPDATE orders.workflow_sessions
             SET lease_owner = {runnerId}, lease_until = {until}, updated_at = {now}
             WHERE (merchant_id, order_id, name) IN (
                 SELECT s.merchant_id, s.order_id, s.name
                 FROM orders.workflow_sessions s
                 WHERE s.name = {name}
                   AND s.complete = false
                   AND (s.lease_until IS NULL OR s.lease_until <= {now})
                 ORDER BY s.updated_at
                 LIMIT 1
                 FOR UPDATE SKIP LOCKED)
             """, ct).ConfigureAwait(false);

        db.ChangeTracker.Clear();

        if (leased == 0) return null;

        // Read back by owner and freshness rather than by the lease timestamp. Postgres
        // keeps a timestamptz to the microsecond and DateTimeOffset to a hundred
        // nanoseconds, so the value written is not always the value that comes back, and an
        // equality on it quietly matches nothing.
        //
        // This assumes a runner holds one session of a workflow at a time, which is what the
        // runner does: it leases, resumes, releases.
        return await db.WorkflowSessions
            .Where(s => s.Name == name && s.LeaseOwner == runnerId && s.LeaseUntil > now)
            .OrderByDescending(s => s.LeaseUntil)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);
    }

    public async Task ReleaseAsync(WorkflowSession session, string runnerId,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentException.ThrowIfNullOrWhiteSpace(runnerId);

        // Only the holder may release, which is why the owner is in the WHERE clause. A
        // runner whose lease has already expired and been taken by somebody else must not
        // be able to unlock the new holder's work.
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"""
             UPDATE orders.workflow_sessions
             SET lease_owner = NULL, lease_until = NULL
             WHERE merchant_id = {session.MerchantId}
               AND order_id = {session.OrderId}
               AND name = {session.Name}
               AND lease_owner = {runnerId}
             """, ct).ConfigureAwait(false);

        db.ChangeTracker.Clear();
    }
}

/// <summary>
/// What makes running an action handler twice have the effect of running it once.
///
/// The row is the claim. Inserting it is the only way to start, and the insert either wins
/// or reports that somebody already has it - so "book the courier for this order" cannot be
/// done twice by two workers, or twice by one worker after a restart.
/// </summary>
public sealed class EfWorkflowActionStore(MoynaPayDbContext db) : IWorkflowActionStore
{
    public async Task<WorkflowActionStart> TryStartAsync(Guid merchantId, Guid orderId,
        string actionId, DateTimeOffset now, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actionId);

        // ON CONFLICT DO NOTHING: the insert either takes the action or reports that it is
        // taken. Checking first and inserting after leaves a window between the two in which
        // both callers see nothing and both proceed.
        var started = await db.Database.ExecuteSqlInterpolatedAsync(
            $"""
             INSERT INTO orders.workflow_actions
                 (merchant_id, order_id, action_id, status, started_at)
             VALUES
                 ({merchantId}, {orderId}, {actionId},
                  {WorkflowActionStatus.Started.ToString()}, {now})
             ON CONFLICT (merchant_id, order_id, action_id) DO NOTHING
             """, ct).ConfigureAwait(false);

        db.ChangeTracker.Clear();

        if (started == 1) return WorkflowActionStart.Started;

        var existing = await FindAsync(merchantId, orderId, actionId, ct).ConfigureAwait(false);

        return existing?.Status == WorkflowActionStatus.Completed
            ? WorkflowActionStart.AlreadyCompleted
            : WorkflowActionStart.AlreadyRunning;
    }

    public async Task CompleteAsync(Guid merchantId, Guid orderId, string actionId, string? result,
        DateTimeOffset now, CancellationToken ct = default)
    {
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"""
             UPDATE orders.workflow_actions
             SET status = {WorkflowActionStatus.Completed.ToString()},
                 completed_at = {now}, result = {result}
             WHERE merchant_id = {merchantId}
               AND order_id = {orderId}
               AND action_id = {actionId}
             """, ct).ConfigureAwait(false);

        db.ChangeTracker.Clear();
    }

    public Task<WorkflowAction?> FindAsync(Guid merchantId, Guid orderId, string actionId,
        CancellationToken ct = default) =>
        db.WorkflowActions.AsNoTracking().FirstOrDefaultAsync(
            a => a.MerchantId == merchantId && a.OrderId == orderId && a.ActionId == actionId, ct);
}

/// <summary>
/// Nonces, in the database rather than in one process's memory.
///
/// Memory was enough while there was one API. With two behind a load balancer a captured
/// request replayed against the other instance is accepted, because that instance never saw
/// the first one.
/// </summary>
public sealed class EfNonceStore(MoynaPayDbContext db) : INonceStore
{
    public async Task<bool> TryUseAsync(string keyId, string nonce, CancellationToken ct = default)
    {
        var id = Guid.CreateVersion7();
        var now = DateTimeOffset.UtcNow;

        // Kept for twice the tolerance, not once.
        //
        // A signature is accepted for the tolerance on EITHER side of the caller's
        // timestamp, and a shop's clock is allowed to be a few minutes out - that is what
        // the tolerance is for. Forgetting a nonce one tolerance after we saw it leaves a
        // window in which the signature is still valid and the nonce is no longer known,
        // which is the replay this row exists to stop.
        var expires = now.AddSeconds(RequestSignature.ToleranceSeconds * 2);

        // Every NOT NULL column is listed. A raw insert skips the conventions EF applies on
        // SaveChanges, so anything left out fails at run time rather than at build time -
        // and the failure looks like every signed request suddenly being refused.
        var inserted = await db.Database.ExecuteSqlInterpolatedAsync(
            $"""
             INSERT INTO merchants.request_nonces
                 (id, tenant_id, key_id, nonce, seen_at, expires_at,
                  created_at, updated_at, is_deleted, row_version)
             VALUES
                 ({id}, {Guid.Empty}, {keyId}, {nonce}, {now}, {expires},
                  {now}, {now}, false, 0)
             ON CONFLICT (key_id, nonce) DO NOTHING
             """, ct).ConfigureAwait(false);

        if (inserted == 1)
        {
            // Swept opportunistically. Nothing older than the window can be replayed, so
            // keeping it grows the table forever for no benefit.
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"DELETE FROM merchants.request_nonces WHERE key_id = {keyId} AND expires_at <= {now}",
                ct).ConfigureAwait(false);
        }

        return inserted == 1;
    }
}
