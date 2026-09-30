using Microsoft.EntityFrameworkCore;
using MoynaPay.Application.Abstractions;
using MoynaPay.Application.Voice;
using MoynaPay.Domain.Orders;

namespace MoynaPay.Infrastructure.Persistence;

/// <summary>
/// Orders, their timeline and the queue of things the shops have not been told yet.
///
/// Two methods here are not translations of the in-memory ones, they are replacements:
/// claiming and releasing a review. In memory those are a lock around a read and a write.
/// Against a database a lock does not exist, so each is one UPDATE whose WHERE clause
/// carries the whole precondition - claimed only if it is still in review and still free -
/// and the row count says whether this caller was the one who won.
/// </summary>
public sealed class EfOrderStore(MoynaPayDbContext db) : IOrderStore
{
    public Task<Order?> FindByReferenceAsync(Guid merchantId, string reference,
        CancellationToken ct = default) =>
        db.Orders.FirstOrDefaultAsync(o => o.TenantId == merchantId && o.Reference == reference, ct);

    public Task<Order?> FindByIdAsync(Guid merchantId, Guid orderId, CancellationToken ct = default) =>
        db.Orders.FirstOrDefaultAsync(o => o.TenantId == merchantId && o.Id == orderId, ct);

    /// <summary>
    /// The order and the event that explains it, in one transaction.
    ///
    /// A unique violation here is not a fault: it is two requests for the same reference
    /// arriving at once, which is exactly what the index is for. The caller looks the
    /// existing order up and answers with it, the same as if it had lost the race by a
    /// second instead of a millisecond.
    /// </summary>
    public async Task SaveNewAsync(Order order, OrderEvent first, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(order);
        ArgumentNullException.ThrowIfNull(first);

        db.Orders.Add(order);
        db.OrderEvents.Add(first);

        try
        {
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        catch (DbUpdateException ex) when (Postgres.IsUniqueViolation(ex))
        {
            db.ChangeTracker.Clear();
            throw new InvalidOperationException("duplicate reference", ex);
        }
    }

    public async Task SaveChangeAsync(Order order, OrderEvent change, OutboxMessage? notify,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(order);
        ArgumentNullException.ThrowIfNull(change);

        db.OrderEvents.Add(change);

        // In the same transaction as the change, always. A notification that exists without
        // the change that caused it, or a change without the notification, is a state
        // nothing downstream can make sense of.
        if (notify is not null) db.OutboxMessages.Add(notify);

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task SaveAuditAsync(Order order, OrderEvent audit, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(order);
        ArgumentNullException.ThrowIfNull(audit);

        db.OrderEvents.Add(audit);

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<OrderCallClaim>> ClaimDueCallsAsync(
        string claimedBy, int max, DateTimeOffset now, DateTimeOffset leaseUntil,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(claimedBy);

        var hours = new CallingHours();
        if (!hours.IsOpen(now)) return [];

        var take = Math.Clamp(max, 1, 100);
        var received = OrderStatus.Received.ToString();
        var calling = OrderStatus.Calling.ToString();

        await db.Database.ExecuteSqlInterpolatedAsync(
            $"""
             UPDATE orders.orders o
             SET status = {calling}, call_attempts = o.call_attempts + 1,
                 claimed_by = {claimedBy}, claimed_until = {leaseUntil},
                 updated_at = {now}, row_version = o.row_version + 1
             WHERE o.id IN (
                 SELECT candidate.id
                 FROM orders.orders candidate
                 INNER JOIN merchants.subscriptions s
                    ON s.tenant_id = candidate.tenant_id
                   AND s.is_deleted = false
                   AND s.calls = true
                 INNER JOIN merchants.merchants m
                    ON m.id = candidate.tenant_id
                   AND m.is_deleted = false
                 WHERE candidate.is_deleted = false
                   AND candidate.status IN ({received}, {calling})
                   AND (candidate.claimed_until IS NULL OR candidate.claimed_until <= {now})
                 ORDER BY candidate.created_at
                 LIMIT {take}
                 FOR UPDATE SKIP LOCKED)
             """, ct).ConfigureAwait(false);

        db.ChangeTracker.Clear();

        var rows = await db.Orders.AsNoTracking()
            .Where(o => o.ClaimedBy == claimedBy)
            .Join(db.Merchants.AsNoTracking(),
                order => order.TenantId,
                merchant => merchant.Id,
                (order, merchant) => new OrderCallClaim(order, merchant.Name))
            .OrderBy(c => c.Order.CreatedAt)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        foreach (var claim in rows)
        {
            db.OrderEvents.Add(new OrderEvent
            {
                TenantId = claim.Order.TenantId,
                OrderId = claim.Order.Id,
                Type = "call.claimed",
                Actor = Actor.Machine,
                From = claim.Order.Status,
                To = OrderStatus.Calling,
                Detail = claimedBy,
                At = now,
            });
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        return rows;
    }

    /// <summary>
    /// Takes the order for one reviewer, or says why it could not.
    ///
    /// The precondition is in the WHERE clause rather than in an if: still in review, and
    /// either unclaimed, or expired, or already this reviewer's. Two staff tapping at the
    /// same moment both send this statement and exactly one of them updates a row, because
    /// the second one's WHERE no longer matches. Checking first and updating after would
    /// let both of them win and leave one customer being rung twice.
    /// </summary>
    public async Task<ReviewClaimStoreResult> TryClaimReviewAsync(Guid merchantId, Guid orderId,
        string reviewer, DateTimeOffset now, TimeSpan claimFor, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reviewer);

        var until = now.Add(claimFor);
        var review = OrderStatus.NeedsHuman.ToString();

        var taken = await db.Database.ExecuteSqlInterpolatedAsync(
            $"""
             UPDATE orders.orders
             SET claimed_by = {reviewer}, claimed_until = {until},
                 updated_at = {now}, row_version = row_version + 1
             WHERE id = {orderId}
               AND tenant_id = {merchantId}
               AND is_deleted = false
               AND status = {review}
               AND (claimed_until IS NULL OR claimed_until <= {now} OR claimed_by = {reviewer})
             """, ct).ConfigureAwait(false);

        // Read after the write, and never from the change tracker: the statement above went
        // straight to the database, so an instance this context loaded earlier still holds
        // the old claim.
        db.ChangeTracker.Clear();

        var order = await FindByIdAsync(merchantId, orderId, ct).ConfigureAwait(false);

        if (order is null)
        {
            return new ReviewClaimStoreResult(ReviewClaimStoreOutcome.NotFound, null, null);
        }

        if (taken == 0)
        {
            return order.Status != OrderStatus.NeedsHuman
                ? new ReviewClaimStoreResult(
                    ReviewClaimStoreOutcome.NotInReview, order, "The order is not waiting for review.")
                : new ReviewClaimStoreResult(
                    ReviewClaimStoreOutcome.AlreadyClaimed, order, "The order is already claimed.");
        }

        db.OrderEvents.Add(new OrderEvent
        {
            TenantId = merchantId,
            OrderId = order.Id,
            Type = "review.claimed",
            Actor = Actor.Merchant,
            ActorName = reviewer,
            From = order.Status,
            To = order.Status,
            At = now,
        });

        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        return new ReviewClaimStoreResult(ReviewClaimStoreOutcome.Claimed, order, null);
    }

    public async Task<ReviewReleaseStoreResult> ReleaseReviewClaimAsync(Guid merchantId,
        Guid orderId, string reviewer, DateTimeOffset now, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reviewer);

        var released = await db.Database.ExecuteSqlInterpolatedAsync(
            $"""
             UPDATE orders.orders
             SET claimed_by = NULL, claimed_until = NULL,
                 updated_at = {now}, row_version = row_version + 1
             WHERE id = {orderId}
               AND tenant_id = {merchantId}
               AND is_deleted = false
               AND claimed_by = {reviewer}
               AND claimed_until IS NOT NULL
               AND claimed_until > {now}
             """, ct).ConfigureAwait(false);

        db.ChangeTracker.Clear();

        var order = await FindByIdAsync(merchantId, orderId, ct).ConfigureAwait(false);

        if (order is null)
        {
            return new ReviewReleaseStoreResult(ReviewReleaseStoreOutcome.NotFound, null, null);
        }

        if (released == 0)
        {
            // Not this reviewer's claim. Either there is no live claim at all - in which
            // case the stale one is tidied away, the same as in memory - or it belongs to
            // somebody else and is left exactly as it is.
            if (order.ClaimedBy is not null
                && order.ClaimedUntil is { } until && until > now
                && !string.Equals(order.ClaimedBy, reviewer, StringComparison.Ordinal))
            {
                return new ReviewReleaseStoreResult(
                    ReviewReleaseStoreOutcome.ClaimedByAnother, order,
                    "The order is claimed by someone else.");
            }

            if (order.ClaimedBy is not null || order.ClaimedUntil is not null)
            {
                order.ClaimedBy = null;
                order.ClaimedUntil = null;
                await db.SaveChangesAsync(ct).ConfigureAwait(false);
            }

            return new ReviewReleaseStoreResult(
                ReviewReleaseStoreOutcome.NotClaimed, order, "The order is not actively claimed.");
        }

        db.OrderEvents.Add(new OrderEvent
        {
            TenantId = merchantId,
            OrderId = order.Id,
            Type = "review.released",
            Actor = Actor.Merchant,
            ActorName = reviewer,
            From = order.Status,
            To = order.Status,
            At = now,
        });

        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        return new ReviewReleaseStoreResult(ReviewReleaseStoreOutcome.Released, order, null);
    }

    public async Task<IReadOnlyList<Order>> ListAsync(Guid merchantId, OrderQuery query,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        var rows = db.Orders.AsNoTracking().Where(o => o.TenantId == merchantId);

        if (query.NeedsHumanOnly) rows = rows.Where(o => o.Status == OrderStatus.NeedsHuman);
        else if (query.Status is { } status) rows = rows.Where(o => o.Status == status);

        if (query.Before is { } before) rows = rows.Where(o => o.CreatedAt < before);
        if (query.From is { } from) rows = rows.Where(o => o.CreatedAt >= from);
        if (query.To is { } to) rows = rows.Where(o => o.CreatedAt <= to);

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            // Escaped before it becomes a pattern. A reference containing a percent sign is
            // a search for that character, not a wildcard that returns the whole table.
            var needle = query.Search.Trim()
                .Replace("\\", "\\\\", StringComparison.Ordinal)
                .Replace("%", "\\%", StringComparison.Ordinal)
                .Replace("_", "\\_", StringComparison.Ordinal);

            var pattern = $"%{needle}%";

            rows = rows.Where(o =>
                EF.Functions.ILike(o.Reference, pattern)
                || EF.Functions.ILike(o.CustomerName, pattern)
                || EF.Functions.Like(o.Msisdn, pattern));
        }

        return await rows
            .OrderByDescending(o => o.CreatedAt)
            .Take(Math.Clamp(query.Take, 1, 200))
            .ToListAsync(ct)
            .ConfigureAwait(false);
    }

    public async Task<IReadOnlyDictionary<OrderStatus, int>> CountByStatusAsync(Guid merchantId,
        CancellationToken ct = default)
    {
        var rows = await db.Orders.AsNoTracking()
            .Where(o => o.TenantId == merchantId)
            .GroupBy(o => o.Status)
            .Select(g => new { Status = g.Key, Count = g.Count() })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return rows.ToDictionary(r => r.Status, r => r.Count);
    }

    /// <summary>
    /// The home screen's numbers, counted from the timeline rather than from the orders.
    ///
    /// "How many were confirmed today" is a question about events, not about state: an
    /// order confirmed this morning and shipped this afternoon is one of each, and counting
    /// current statuses would show only the shipment.
    ///
    /// The week's events are fetched and counted here rather than in six SQL aggregates.
    /// One merchant's week is a few hundred rows, and six round trips - or one query the
    /// provider might refuse to translate - buys nothing against that.
    /// </summary>
    public async Task<HomeMetrics> HomeMetricsAsync(Guid merchantId, DateTimeOffset todayStart,
        DateTimeOffset sevenDayStart, DateTimeOffset now, CancellationToken ct = default)
    {
        var events = await db.OrderEvents.AsNoTracking()
            .Where(e => e.TenantId == merchantId && e.At >= sevenDayStart && e.At <= now)
            .Select(e => new EventFact(e.At, e.Type, e.To))
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return new HomeMetrics(
            todayStart,
            sevenDayStart,
            now,
            Count(events.Where(e => e.At >= todayStart)),
            Count(events));

        static HomeMetricWindow Count(IEnumerable<EventFact> source)
        {
            var rows = source.ToList();

            return new HomeMetricWindow(
                rows.Count(e => e.Type == "order.received"),
                rows.Count(e => e.To == OrderStatus.Confirmed),
                rows.Count(e => e.To == OrderStatus.NeedsHuman),
                rows.Count(e => e.To == OrderStatus.Paid),
                rows.Count(e => e.To == OrderStatus.Shipped),
                rows.Count(e => e.Type == "call.failed"));
        }
    }

    public async Task<IReadOnlyList<OrderEvent>> TimelineAsync(Guid merchantId, Guid orderId,
        CancellationToken ct = default) =>
        await db.OrderEvents.AsNoTracking()
            .Where(e => e.TenantId == merchantId && e.OrderId == orderId)
            .OrderBy(e => e.At)
            .ToListAsync(ct)
            .ConfigureAwait(false);

    private sealed record EventFact(DateTimeOffset At, string Type, OrderStatus? To);
}
