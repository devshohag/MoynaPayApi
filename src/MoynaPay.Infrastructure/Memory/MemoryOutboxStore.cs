using MoynaPay.Application.Abstractions;
using MoynaPay.Domain.Orders;

namespace MoynaPay.Infrastructure.Memory;

/// <summary>
/// The outbox queue, in memory, with the same two rules the SQL one enforces: one
/// dispatcher may hold a message at a time, and an order's messages leave in the order they
/// were written.
///
/// This is not a stand-in for the real one. It is where the ordering rule is actually
/// legible - the SQL version says the same thing in a NOT EXISTS clause - and it is what
/// makes the dispatcher's whole retry ladder checkable without a database.
/// </summary>
public sealed class MemoryOutboxStore(MemoryDatabase db) : IOutboxStore
{
    public Task<IReadOnlyList<OutboxMessage>> ClaimDueAsync(
        string claimedBy, int max, DateTimeOffset now, DateTimeOffset leaseUntil,
        CancellationToken ct = default)
    {
        lock (db.Gate)
        {
            var open = db.Outbox
                .Where(m => !m.IsDeleted && !m.IsDead && m.DeliveredAt is null)
                .ToList();

            // The oldest still-open message for each order. Anything behind it waits, so a
            // shop can never be told an order was paid before it is told it was confirmed.
            var heads = open
                .GroupBy(m => m.OrderId)
                .Select(g => g.OrderBy(m => m.CreatedAt).ThenBy(m => m.Id).First());

            IReadOnlyList<OutboxMessage> claimed = heads
                .Where(m => m.NextAttemptAt <= now)
                .Where(m => m.ClaimedUntil is null || m.ClaimedUntil <= now)
                .OrderBy(m => m.NextAttemptAt)
                .Take(Math.Clamp(max, 1, 500))
                .ToList();

            foreach (var message in claimed)
            {
                message.ClaimedBy = claimedBy;
                message.ClaimedUntil = leaseUntil;
            }

            return Task.FromResult(claimed);
        }
    }

    public Task FinishAsync(OutboxMessage message, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(message);

        // The dispatcher mutated the instance this store handed it, which is the whole point
        // of an in-memory store. The lock is still taken so a reader on another thread sees
        // a whole message rather than half of one.
        lock (db.Gate)
        {
            if (!db.Outbox.Contains(message)) db.Outbox.Add(message);
        }

        return Task.CompletedTask;
    }
}
