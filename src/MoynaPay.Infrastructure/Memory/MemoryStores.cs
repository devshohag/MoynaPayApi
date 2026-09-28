using System.Collections.Concurrent;
using MoynaPay.Application.Abstractions;
using MoynaPay.Domain.Merchants;
using MoynaPay.Domain.Orders;

namespace MoynaPay.Infrastructure.Memory;

/// <summary>
/// The stores, in memory.
///
/// Not a throwaway. It is what lets the API be started and exercised end to end on any
/// machine - no SQL Server, no connection string, no migration - and it is what the tests
/// run against, so the rules are checked thousands of times a second instead of a few
/// times a minute.
///
/// The EF Core implementation is a sibling, not a replacement: both satisfy the same
/// ports, and the day one behaves differently from the other, the tests here are what
/// says so.
/// </summary>
public sealed class MemoryDatabase
{
    public ConcurrentDictionary<Guid, Merchant> Merchants { get; } = new();
    public ConcurrentDictionary<Guid, Subscription> Subscriptions { get; } = new();
    public ConcurrentDictionary<string, ApiCredential> Credentials { get; } = new(StringComparer.Ordinal);
    public ConcurrentDictionary<Guid, WebhookEndpoint> Webhooks { get; } = new();

    public ConcurrentDictionary<Guid, Order> Orders { get; } = new();
    public List<OrderEvent> Events { get; } = [];
    public List<OutboxMessage> Outbox { get; } = [];

    internal readonly object Gate = new();
}

public sealed class MemoryMerchantStore(MemoryDatabase db) : IMerchantStore
{
    public Task<Merchant?> FindAsync(Guid merchantId, CancellationToken ct = default) =>
        Task.FromResult(db.Merchants.TryGetValue(merchantId, out var m) && !m.IsDeleted ? m : null);

    public Task<Subscription> SubscriptionAsync(Guid merchantId, CancellationToken ct = default) =>
        Task.FromResult(db.Subscriptions.TryGetValue(merchantId, out var s)
            ? s
            // A merchant with no row has bought nothing. Returning an empty subscription
            // rather than throwing keeps the pipeline's answer defined: skip everything.
            : new Subscription { TenantId = merchantId });

    public Task<ApiCredential?> FindCredentialAsync(string keyId, CancellationToken ct = default) =>
        Task.FromResult(db.Credentials.TryGetValue(keyId, out var c) && c.IsActive ? c : null);

    public Task<WebhookEndpoint?> WebhookAsync(Guid merchantId, CancellationToken ct = default) =>
        Task.FromResult(db.Webhooks.TryGetValue(merchantId, out var w) && !w.IsDeleted ? w : null);
}

public sealed class MemoryOrderStore(MemoryDatabase db) : IOrderStore
{
    public Task<Order?> FindByReferenceAsync(Guid merchantId, string reference, CancellationToken ct = default)
    {
        var found = db.Orders.Values.FirstOrDefault(
            o => o.TenantId == merchantId && !o.IsDeleted
              && string.Equals(o.Reference, reference, StringComparison.Ordinal));

        return Task.FromResult(found);
    }

    public Task<Order?> FindByIdAsync(Guid merchantId, Guid orderId, CancellationToken ct = default) =>
        Task.FromResult(db.Orders.TryGetValue(orderId, out var o)
            && o.TenantId == merchantId && !o.IsDeleted ? o : null);

    public Task SaveNewAsync(Order order, OrderEvent first, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(order);
        ArgumentNullException.ThrowIfNull(first);

        lock (db.Gate)
        {
            // The uniqueness that the real schema enforces with an index. Checked here too,
            // so a race in a test fails the same way it would fail in production rather
            // than quietly producing two orders with one reference.
            var clash = db.Orders.Values.Any(
                o => o.TenantId == order.TenantId && !o.IsDeleted
                  && string.Equals(o.Reference, order.Reference, StringComparison.Ordinal));

            if (clash) throw new InvalidOperationException("duplicate reference");

            db.Orders[order.Id] = order;
            db.Events.Add(first);
        }

        return Task.CompletedTask;
    }

    public Task SaveChangeAsync(Order order, OrderEvent change, OutboxMessage? notify,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(order);
        ArgumentNullException.ThrowIfNull(change);

        lock (db.Gate)
        {
            db.Orders[order.Id] = order;
            db.Events.Add(change);

            // One lock around all three, because the real store writes them in one
            // transaction: a notification that exists without its change, or a change
            // without its audit row, is a state nothing downstream can explain.
            if (notify is not null) db.Outbox.Add(notify);
        }

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<Order>> ListAsync(Guid merchantId, OrderQuery query,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        var rows = db.Orders.Values.Where(o => o.TenantId == merchantId && !o.IsDeleted);

        if (query.NeedsHumanOnly) rows = rows.Where(o => o.Status == OrderStatus.NeedsHuman);
        else if (query.Status is { } status) rows = rows.Where(o => o.Status == status);

        if (query.Before is { } before) rows = rows.Where(o => o.CreatedAt < before);

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var needle = query.Search.Trim();

            rows = rows.Where(o =>
                o.Reference.Contains(needle, StringComparison.OrdinalIgnoreCase)
                || o.CustomerName.Contains(needle, StringComparison.OrdinalIgnoreCase)
                || o.Msisdn.Contains(needle, StringComparison.Ordinal));
        }

        IReadOnlyList<Order> page = rows
            .OrderByDescending(o => o.CreatedAt)
            .Take(Math.Clamp(query.Take, 1, 200))
            .ToList();

        return Task.FromResult(page);
    }

    public Task<IReadOnlyList<OrderEvent>> TimelineAsync(Guid merchantId, Guid orderId,
        CancellationToken ct = default)
    {
        lock (db.Gate)
        {
            IReadOnlyList<OrderEvent> rows = db.Events
                .Where(e => e.TenantId == merchantId && e.OrderId == orderId)
                .OrderBy(e => e.At)
                .ToList();

            return Task.FromResult(rows);
        }
    }
}

/// <summary>
/// Nonces, remembered only as long as the clock still accepts them.
///
/// Anything older than the tolerance is already refused by the timestamp check, so
/// keeping it buys no safety and costs memory that grows forever.
/// </summary>
public sealed class MemoryNonceStore(IClock clock) : INonceStore
{
    private readonly ConcurrentDictionary<string, DateTimeOffset> _seen = new(StringComparer.Ordinal);

    public Task<bool> TryUseAsync(string keyId, string nonce, CancellationToken ct = default)
    {
        var now = clock.UtcNow;

        if (_seen.Count > 10_000)
        {
            var cutoff = now.AddSeconds(-MoynaPay.Application.Security.RequestSignature.ToleranceSeconds);

            foreach (var pair in _seen)
            {
                if (pair.Value < cutoff) _seen.TryRemove(pair.Key, out _);
            }
        }

        return Task.FromResult(_seen.TryAdd($"{keyId}:{nonce}", now));
    }
}

/// <summary>
/// Development only: keeps the secret as it is and calls that protection.
///
/// It exists so the service starts with no key ring, and it says what it is rather than
/// looking like encryption. The real one is AES-GCM with a key ring, the same shape YoPay
/// uses; a deployed instance must refuse to start with this.
/// </summary>
public sealed class PlaintextSecretProtector : ISecretProtector
{
    public const string KeyRing = "dev-plaintext";

    public string Protect(string plaintext, out string keyRingId)
    {
        keyRingId = KeyRing;
        return plaintext;
    }

    public string Unprotect(string cipher, string keyRingId) => cipher;
}
