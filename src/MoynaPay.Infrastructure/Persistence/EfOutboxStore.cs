using Microsoft.EntityFrameworkCore;
using MoynaPay.Application.Abstractions;
using MoynaPay.Domain.Orders;

namespace MoynaPay.Infrastructure.Persistence;

/// <summary>
/// The outbox queue, against Postgres.
///
/// Claiming is one statement, not a transaction held open across the delivery. That one
/// statement is the whole concurrency story:
///
///   FOR UPDATE SKIP LOCKED  - two dispatchers running at once take different rows rather
///                             than queueing behind each other or, worse, both taking the
///                             same one and telling the shop twice.
///   NOT EXISTS (older ...)  - an order's messages leave in the order they were written.
///   claimed_until           - a dispatcher that dies does not hold its rows forever.
/// </summary>
public sealed class EfOutboxStore(MoynaPayDbContext db) : IOutboxStore
{
    public async Task<IReadOnlyList<OutboxMessage>> ClaimDueAsync(
        string claimedBy, int max, DateTimeOffset now, DateTimeOffset leaseUntil,
        CancellationToken ct = default)
    {
        var take = Math.Clamp(max, 1, 500);

        // Two statements rather than one UPDATE ... RETURNING, because EF composes a FromSql
        // query into a subquery and a data-modifying statement cannot sit in one. The claim
        // is still atomic: the UPDATE is a single statement, so the rows it takes are taken
        // under one implicit transaction, and the read afterwards only asks which rows carry
        // this pass's mark.
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"""
             UPDATE orders.outbox_messages m
             SET claimed_by = {claimedBy}, claimed_until = {leaseUntil},
                 updated_at = {now}, row_version = m.row_version + 1
             WHERE m.id IN (
                 SELECT o.id
                 FROM orders.outbox_messages o
                 WHERE o.is_deleted = false
                   AND o.is_dead = false
                   AND o.delivered_at IS NULL
                   AND o.next_attempt_at <= {now}
                   AND (o.claimed_until IS NULL OR o.claimed_until <= {now})
                   AND NOT EXISTS (
                       SELECT 1
                       FROM orders.outbox_messages e
                       WHERE e.order_id = o.order_id
                         AND e.is_deleted = false
                         AND e.is_dead = false
                         AND e.delivered_at IS NULL
                         AND (e.created_at, e.id) < (o.created_at, o.id))
                 ORDER BY o.next_attempt_at
                 LIMIT {take}
                 FOR UPDATE SKIP LOCKED)
             """, ct).ConfigureAwait(false);

        db.ChangeTracker.Clear();

        // Read back by the mark, which is why the caller's claim id has to be unique per
        // pass and not just per worker.
        //
        // Matching on claimed_until instead would look tidier and would fail: Postgres keeps
        // a timestamptz to the microsecond and DateTimeOffset to a hundred nanoseconds, so
        // the value that goes in is not always the value that comes back out, and the
        // equality quietly matches nothing. A string does not round.
        return await db.OutboxMessages
            .Where(m => m.ClaimedBy == claimedBy)
            .OrderBy(m => m.CreatedAt)
            .ToListAsync(ct)
            .ConfigureAwait(false);
    }

    public Task FinishAsync(OutboxMessage message, CancellationToken ct = default) =>
        // The dispatcher changed the tracked instance this store returned, so this is a
        // save, not a write. The concurrency token still applies: if another dispatcher took
        // the row after its lease expired and finished first, this throws rather than
        // overwriting their result with a stale one.
        db.SaveChangesAsync(ct);
}
