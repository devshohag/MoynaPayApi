using System.Collections.Concurrent;
using MoynaPay.Application.AppAuth;
using MoynaPay.Application.Abstractions;
using MoynaPay.Application.Voice;
using MoynaPay.Domain.Merchants;
using MoynaPay.Domain.Orders;
using MoynaPay.Domain.Payments;

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
    public ConcurrentDictionary<Guid, AppOtpChallenge> AppOtps { get; } = new();
    public ConcurrentDictionary<string, AppToken> AppTokens { get; } = new(StringComparer.Ordinal);
    public ConcurrentDictionary<string, List<DateTimeOffset>> RateLimits { get; } = new(StringComparer.Ordinal);
    public ConcurrentDictionary<string, AppDevicePairingToken> AppDevicePairingTokens { get; } =
        new(StringComparer.Ordinal);
    public ConcurrentDictionary<Guid, AppDevice> AppDevices { get; } = new();

    public ConcurrentDictionary<Guid, Order> Orders { get; } = new();
    public ConcurrentDictionary<Guid, Invoice> Invoices { get; } = new();
    public List<OrderEvent> Events { get; } = [];
    public List<OutboxMessage> Outbox { get; } = [];
    public ConcurrentDictionary<string, WorkflowSession> WorkflowSessions { get; } = new(StringComparer.Ordinal);
    public ConcurrentDictionary<string, WorkflowAction> WorkflowActions { get; } = new(StringComparer.Ordinal);

    internal readonly object Gate = new();
}

public sealed class MemoryMerchantStore(MemoryDatabase db) : IMerchantStore
{
    public Task<Merchant?> FindAsync(Guid merchantId, CancellationToken ct = default) =>
        Task.FromResult(db.Merchants.TryGetValue(merchantId, out var m) && !m.IsDeleted ? m : null);

    public Task<Merchant?> FindByMsisdnAsync(string msisdn, CancellationToken ct = default)
    {
        var merchant = db.Merchants.Values.FirstOrDefault(m =>
            !m.IsDeleted && string.Equals(m.Msisdn, msisdn, StringComparison.Ordinal));

        return Task.FromResult(merchant);
    }

    public Task SaveMerchantAsync(Merchant merchant, Subscription subscription, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(merchant);
        ArgumentNullException.ThrowIfNull(subscription);

        lock (db.Gate)
        {
            if (db.Merchants.ContainsKey(merchant.Id))
            {
                throw new InvalidOperationException("duplicate merchant");
            }

            db.Merchants[merchant.Id] = merchant;
            db.Subscriptions[merchant.Id] = subscription;
        }

        return Task.CompletedTask;
    }

    public Task SaveMerchantProfileAsync(Merchant merchant, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(merchant);

        lock (db.Gate)
        {
            if (!db.Merchants.TryGetValue(merchant.Id, out var existing)
                || existing.TenantId != merchant.TenantId
                || existing.IsDeleted)
            {
                throw new InvalidOperationException("merchant not found");
            }

            db.Merchants[merchant.Id] = merchant;
        }

        return Task.CompletedTask;
    }

    public Task<Subscription> SubscriptionAsync(Guid merchantId, CancellationToken ct = default) =>
        Task.FromResult(db.Subscriptions.TryGetValue(merchantId, out var s)
            ? s
            // A merchant with no row has bought nothing. Returning an empty subscription
            // rather than throwing keeps the pipeline's answer defined: skip everything.
            : new Subscription { TenantId = merchantId });

    public Task<ApiCredential?> FindCredentialAsync(string keyId, CancellationToken ct = default) =>
        Task.FromResult(db.Credentials.TryGetValue(keyId, out var c) && c.IsActive ? c : null);

    public Task<IReadOnlyList<ApiCredential>> ListCredentialsAsync(Guid merchantId, CancellationToken ct = default)
    {
        IReadOnlyList<ApiCredential> rows = db.Credentials.Values
            .Where(c => c.TenantId == merchantId && !c.IsDeleted)
            .OrderByDescending(c => c.CreatedAt)
            .ToList();

        return Task.FromResult(rows);
    }

    public Task<bool> HasAnyCredentialAsync(Guid merchantId, CancellationToken ct = default) =>
        Task.FromResult(db.Credentials.Values.Any(c => c.TenantId == merchantId && !c.IsDeleted));

    public Task SaveCredentialAsync(ApiCredential credential, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(credential);

        if (!db.Credentials.TryAdd(credential.KeyId, credential))
        {
            throw new InvalidOperationException("duplicate key");
        }

        return Task.CompletedTask;
    }

    public Task<bool> RevokeCredentialAsync(Guid merchantId, string keyId, DateTimeOffset at,
        CancellationToken ct = default)
    {
        if (!db.Credentials.TryGetValue(keyId, out var credential)
            || credential.TenantId != merchantId
            || credential.IsDeleted
            || credential.RevokedAt is not null)
        {
            return Task.FromResult(false);
        }

        credential.RevokedAt = at;
        credential.UpdatedAt = at;
        db.Credentials[keyId] = credential;

        return Task.FromResult(true);
    }

    public Task<WebhookEndpoint?> WebhookAsync(Guid merchantId, CancellationToken ct = default) =>
        Task.FromResult(db.Webhooks.TryGetValue(merchantId, out var w) && !w.IsDeleted ? w : null);

    public Task SaveWebhookAsync(WebhookEndpoint endpoint, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(endpoint);

        db.Webhooks[endpoint.TenantId] = endpoint;

        return Task.CompletedTask;
    }

    public Task UpdateWebhookDeliveryAsync(Guid merchantId, DateTimeOffset? deliveredAt,
        string? failureReason, CancellationToken ct = default)
    {
        if (!db.Webhooks.TryGetValue(merchantId, out var endpoint) || endpoint.IsDeleted)
        {
            return Task.CompletedTask;
        }

        endpoint.LastDeliveredAt = deliveredAt ?? endpoint.LastDeliveredAt;
        endpoint.LastFailureReason = failureReason;
        endpoint.UpdatedAt = deliveredAt ?? DateTimeOffset.UtcNow;
        db.Webhooks[merchantId] = endpoint;

        return Task.CompletedTask;
    }
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

    public Task SaveAuditAsync(Order order, OrderEvent audit, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(order);
        ArgumentNullException.ThrowIfNull(audit);

        lock (db.Gate)
        {
            db.Orders[order.Id] = order;
            db.Events.Add(audit);
        }

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<OrderCallClaim>> ClaimDueCallsAsync(
        string claimedBy, int max, DateTimeOffset now, DateTimeOffset leaseUntil,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(claimedBy);

        var take = Math.Clamp(max, 1, 100);
        var hours = new CallingHours();

        if (!hours.IsOpen(now))
        {
            return Task.FromResult<IReadOnlyList<OrderCallClaim>>([]);
        }

        lock (db.Gate)
        {
            var claims = new List<OrderCallClaim>(take);

            foreach (var order in db.Orders.Values
                         .Where(IsDue)
                         .OrderBy(o => o.CreatedAt)
                         .Take(take))
            {
                if (!db.Merchants.TryGetValue(order.TenantId, out var merchant)
                    || merchant.IsDeleted)
                {
                    continue;
                }

                var from = order.Status;

                order.Status = OrderStatus.Calling;
                order.ClaimedBy = claimedBy;
                order.ClaimedUntil = leaseUntil;
                order.UpdatedAt = now;
                db.Orders[order.Id] = order;

                db.Events.Add(new OrderEvent
                {
                    TenantId = order.TenantId,
                    OrderId = order.Id,
                    Type = "call.claimed",
                    Actor = Actor.Machine,
                    From = from,
                    To = OrderStatus.Calling,
                    Detail = claimedBy,
                    At = now,
                });

                claims.Add(new OrderCallClaim(order, merchant.Name));
            }

            return Task.FromResult<IReadOnlyList<OrderCallClaim>>(claims);
        }

        bool IsDue(Order order) =>
            !order.IsDeleted
            && order.Status is OrderStatus.Received or OrderStatus.Calling
            && (!order.IsClaimed(now) || string.Equals(order.ClaimedBy, claimedBy, StringComparison.Ordinal))
            && db.Subscriptions.TryGetValue(order.TenantId, out var subscription)
            && subscription.Calls;
    }

    public Task<ReviewClaimStoreResult> TryClaimReviewAsync(Guid merchantId, Guid orderId,
        string reviewer, DateTimeOffset now, TimeSpan claimFor, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reviewer);

        lock (db.Gate)
        {
            if (!db.Orders.TryGetValue(orderId, out var order)
                || order.TenantId != merchantId
                || order.IsDeleted)
            {
                return Task.FromResult(new ReviewClaimStoreResult(
                    ReviewClaimStoreOutcome.NotFound, null, null));
            }

            if (order.Status != OrderStatus.NeedsHuman)
            {
                return Task.FromResult(new ReviewClaimStoreResult(
                    ReviewClaimStoreOutcome.NotInReview, order, "The order is not waiting for review."));
            }

            if (order.IsClaimed(now) && !string.Equals(order.ClaimedBy, reviewer, StringComparison.Ordinal))
            {
                return Task.FromResult(new ReviewClaimStoreResult(
                    ReviewClaimStoreOutcome.AlreadyClaimed, order, "The order is already claimed."));
            }

            order.ClaimedBy = reviewer;
            order.ClaimedUntil = now.Add(claimFor);
            order.UpdatedAt = now;
            db.Orders[order.Id] = order;
            db.Events.Add(new OrderEvent
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

            return Task.FromResult(new ReviewClaimStoreResult(
                ReviewClaimStoreOutcome.Claimed, order, null));
        }
    }

    public Task<ReviewReleaseStoreResult> ReleaseReviewClaimAsync(Guid merchantId, Guid orderId,
        string reviewer, DateTimeOffset now, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reviewer);

        lock (db.Gate)
        {
            if (!db.Orders.TryGetValue(orderId, out var order)
                || order.TenantId != merchantId
                || order.IsDeleted)
            {
                return Task.FromResult(new ReviewReleaseStoreResult(
                    ReviewReleaseStoreOutcome.NotFound, null, null));
            }

            if (order.ClaimedBy is null || order.ClaimedUntil is null || order.ClaimedUntil <= now)
            {
                order.ClaimedBy = null;
                order.ClaimedUntil = null;
                db.Orders[order.Id] = order;

                return Task.FromResult(new ReviewReleaseStoreResult(
                    ReviewReleaseStoreOutcome.NotClaimed, order, "The order is not actively claimed."));
            }

            if (!string.Equals(order.ClaimedBy, reviewer, StringComparison.Ordinal))
            {
                return Task.FromResult(new ReviewReleaseStoreResult(
                    ReviewReleaseStoreOutcome.ClaimedByAnother, order, "The order is claimed by someone else."));
            }

            order.ClaimedBy = null;
            order.ClaimedUntil = null;
            order.UpdatedAt = now;
            db.Orders[order.Id] = order;
            db.Events.Add(new OrderEvent
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

            return Task.FromResult(new ReviewReleaseStoreResult(
                ReviewReleaseStoreOutcome.Released, order, null));
        }
    }

    public Task<IReadOnlyList<Order>> ListAsync(Guid merchantId, OrderQuery query,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        var rows = db.Orders.Values.Where(o => o.TenantId == merchantId && !o.IsDeleted);

        if (query.NeedsHumanOnly) rows = rows.Where(o => o.Status == OrderStatus.NeedsHuman);
        else if (query.Status is { } status) rows = rows.Where(o => o.Status == status);

        if (query.Before is { } before) rows = rows.Where(o => o.CreatedAt < before);
        if (query.From is { } from) rows = rows.Where(o => o.CreatedAt >= from);
        if (query.To is { } to) rows = rows.Where(o => o.CreatedAt <= to);

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

    public Task<IReadOnlyDictionary<OrderStatus, int>> CountByStatusAsync(Guid merchantId,
        CancellationToken ct = default)
    {
        IReadOnlyDictionary<OrderStatus, int> counts = db.Orders.Values
            .Where(o => o.TenantId == merchantId && !o.IsDeleted)
            .GroupBy(o => o.Status)
            .ToDictionary(g => g.Key, g => g.Count());

        return Task.FromResult(counts);
    }

    public Task<HomeMetrics> HomeMetricsAsync(Guid merchantId, DateTimeOffset todayStart,
        DateTimeOffset sevenDayStart, DateTimeOffset now, CancellationToken ct = default)
    {
        lock (db.Gate)
        {
            var events = db.Events
                .Where(e => e.TenantId == merchantId && e.At >= sevenDayStart && e.At <= now)
                .ToList();

            return Task.FromResult(new HomeMetrics(
                todayStart,
                sevenDayStart,
                now,
                Count(events.Where(e => e.At >= todayStart)),
                Count(events)));
        }

        static HomeMetricWindow Count(IEnumerable<OrderEvent> events)
        {
            var rows = events.ToList();

            return new HomeMetricWindow(
                rows.Count(e => e.Type == "order.received"),
                rows.Count(e => e.To == OrderStatus.Confirmed),
                rows.Count(e => e.To == OrderStatus.NeedsHuman),
                rows.Count(e => e.To == OrderStatus.Paid),
                rows.Count(e => e.To == OrderStatus.Shipped),
                rows.Count(e => e.Type == "call.failed"));
        }
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

public sealed class MemoryInvoiceStore(MemoryDatabase db) : IInvoiceStore
{
    public Task<Invoice?> FindByOrderRefAsync(Guid merchantId, string orderRef, CancellationToken ct = default)
    {
        var invoice = db.Invoices.Values.FirstOrDefault(i =>
            i.TenantId == merchantId
            && !i.IsDeleted
            && string.Equals(i.OrderRef, orderRef, StringComparison.Ordinal));

        return Task.FromResult(invoice);
    }

    public Task SaveAsync(Invoice invoice, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(invoice);

        db.Invoices[invoice.Id] = invoice;

        return Task.CompletedTask;
    }
}

public sealed class MemoryAppAuthStore(MemoryDatabase db) : IAppAuthStore
{
    public Task SaveOtpAsync(AppOtpChallenge challenge, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(challenge);

        db.AppOtps[challenge.Id] = challenge;

        return Task.CompletedTask;
    }

    public Task<AppOtpChallenge?> LatestOtpAsync(string msisdn, CancellationToken ct = default)
    {
        var challenge = db.AppOtps.Values
            .Where(o => string.Equals(o.Msisdn, msisdn, StringComparison.Ordinal))
            .OrderByDescending(o => o.CreatedAt)
            .FirstOrDefault();

        return Task.FromResult(challenge);
    }

    public Task SaveTokenAsync(AppToken token, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(token);

        db.AppTokens[Key(token.TokenHash, token.Kind)] = token;

        return Task.CompletedTask;
    }

    public Task<AppToken?> FindTokenAsync(string tokenHash, AppTokenKind kind,
        CancellationToken ct = default)
    {
        db.AppTokens.TryGetValue(Key(tokenHash, kind), out var token);

        return Task.FromResult(token);
    }

    public Task RevokeTokenAsync(string tokenHash, AppTokenKind kind, DateTimeOffset now,
        string? replacedByHash = null, CancellationToken ct = default)
    {
        lock (db.Gate)
        {
            if (db.AppTokens.TryGetValue(Key(tokenHash, kind), out var token)
                && token.RevokedAt is null)
            {
                token.RevokedAt = now;
                token.ReplacedByHash = replacedByHash;
                db.AppTokens[Key(tokenHash, kind)] = token;
            }
        }

        return Task.CompletedTask;
    }

    private static string Key(string tokenHash, AppTokenKind kind) => $"{kind}:{tokenHash}";
}

public sealed class MemoryRateLimitStore(MemoryDatabase db) : IRateLimitStore
{
    public Task<bool> TryConsumeAsync(string key, DateTimeOffset now, TimeSpan window, int limit,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        if (limit <= 0) return Task.FromResult(false);

        lock (db.Gate)
        {
            var bucket = db.RateLimits.GetOrAdd(key, _ => []);
            var cutoff = now.Subtract(window);
            bucket.RemoveAll(stamp => stamp <= cutoff);

            if (bucket.Count >= limit) return Task.FromResult(false);

            bucket.Add(now);
            return Task.FromResult(true);
        }
    }
}

public sealed class MemoryAppDeviceStore(MemoryDatabase db) : IAppDeviceStore
{
    public Task SavePairingTokenAsync(AppDevicePairingToken token, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(token);

        db.AppDevicePairingTokens[token.TokenHash] = token;

        return Task.CompletedTask;
    }

    public Task<AppDevicePairingToken?> FindPairingTokenAsync(string tokenHash,
        CancellationToken ct = default)
    {
        db.AppDevicePairingTokens.TryGetValue(tokenHash, out var token);

        return Task.FromResult(token);
    }

    public Task ConsumePairingTokenAsync(AppDevicePairingToken token, Guid deviceId,
        DateTimeOffset now, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(token);

        lock (db.Gate)
        {
            if (db.AppDevicePairingTokens.TryGetValue(token.TokenHash, out var current)
                && current.ConsumedAt is null)
            {
                current.ConsumedAt = now;
                current.DeviceId = deviceId;
                db.AppDevicePairingTokens[current.TokenHash] = current;
            }
        }

        return Task.CompletedTask;
    }

    public Task SaveDeviceAsync(AppDevice device, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(device);

        db.AppDevices[device.Id] = device;

        return Task.CompletedTask;
    }

    public Task<AppDevice?> FindDeviceAsync(Guid merchantId, Guid deviceId,
        CancellationToken ct = default)
    {
        db.AppDevices.TryGetValue(deviceId, out var device);

        return Task.FromResult(device is not null && device.MerchantId == merchantId
            && device.IsActive ? device : null);
    }

    public Task<AppDevice?> FindByCredentialAsync(Guid deviceId, string deviceTokenHash,
        CancellationToken ct = default)
    {
        db.AppDevices.TryGetValue(deviceId, out var device);

        return Task.FromResult(device is not null
            && device.IsActive
            && string.Equals(device.DeviceTokenHash, deviceTokenHash, StringComparison.Ordinal)
            ? device : null);
    }

    public Task<IReadOnlyList<AppDevice>> ListDevicesAsync(Guid merchantId,
        CancellationToken ct = default)
    {
        IReadOnlyList<AppDevice> rows = db.AppDevices.Values
            .Where(d => d.MerchantId == merchantId && d.IsActive)
            .OrderByDescending(d => d.LastHeartbeatAt ?? d.CreatedAt)
            .ToList();

        return Task.FromResult(rows);
    }
}

public sealed class MemoryWorkflowSessionStore(MemoryDatabase db) : IWorkflowSessionStore
{
    public Task<WorkflowSession?> FindAsync(Guid merchantId, Guid orderId, string name,
        CancellationToken ct = default)
    {
        db.WorkflowSessions.TryGetValue(Key(merchantId, orderId, name), out var session);
        return Task.FromResult(session);
    }

    public Task SaveAsync(WorkflowSession session, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(session);

        db.WorkflowSessions[Key(session.MerchantId, session.OrderId, session.Name)] = session;

        return Task.CompletedTask;
    }

    public Task<WorkflowSession?> TryLeaseNextAsync(string name, string runnerId, DateTimeOffset now,
        TimeSpan leaseFor, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(runnerId);

        lock (db.Gate)
        {
            var session = db.WorkflowSessions.Values
                .Where(s => s.Name == name && !s.Complete
                    && (s.LeaseUntil is null || s.LeaseUntil <= now))
                .OrderBy(s => s.UpdatedAt)
                .FirstOrDefault();

            if (session is null) return Task.FromResult<WorkflowSession?>(null);

            session.LeaseOwner = runnerId;
            session.LeaseUntil = now.Add(leaseFor);
            session.UpdatedAt = now;
            db.WorkflowSessions[Key(session.MerchantId, session.OrderId, session.Name)] = session;

            return Task.FromResult<WorkflowSession?>(session);
        }
    }

    public Task ReleaseAsync(WorkflowSession session, string runnerId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentException.ThrowIfNullOrWhiteSpace(runnerId);

        lock (db.Gate)
        {
            if (db.WorkflowSessions.TryGetValue(
                    Key(session.MerchantId, session.OrderId, session.Name), out var current)
                && current.LeaseOwner == runnerId)
            {
                current.LeaseOwner = null;
                current.LeaseUntil = null;
                db.WorkflowSessions[Key(current.MerchantId, current.OrderId, current.Name)] = current;
            }
        }

        return Task.CompletedTask;
    }

    private static string Key(Guid merchantId, Guid orderId, string name) =>
        $"{merchantId:N}:{orderId:N}:{name}";
}

public sealed class MemoryWorkflowActionStore(MemoryDatabase db) : IWorkflowActionStore
{
    public Task<WorkflowActionStart> TryStartAsync(Guid merchantId, Guid orderId, string actionId,
        DateTimeOffset now, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actionId);

        lock (db.Gate)
        {
            var key = Key(merchantId, orderId, actionId);
            if (db.WorkflowActions.TryGetValue(key, out var existing))
            {
                return Task.FromResult(existing.Status == WorkflowActionStatus.Completed
                    ? WorkflowActionStart.AlreadyCompleted
                    : WorkflowActionStart.AlreadyRunning);
            }

            db.WorkflowActions[key] = new WorkflowAction
            {
                MerchantId = merchantId,
                OrderId = orderId,
                ActionId = actionId,
                StartedAt = now,
            };

            return Task.FromResult(WorkflowActionStart.Started);
        }
    }

    public Task CompleteAsync(Guid merchantId, Guid orderId, string actionId, string? result,
        DateTimeOffset now, CancellationToken ct = default)
    {
        lock (db.Gate)
        {
            var key = Key(merchantId, orderId, actionId);
            if (!db.WorkflowActions.TryGetValue(key, out var action)) return Task.CompletedTask;

            action.Status = WorkflowActionStatus.Completed;
            action.CompletedAt = now;
            action.Result = result;
            db.WorkflowActions[key] = action;
        }

        return Task.CompletedTask;
    }

    public Task<WorkflowAction?> FindAsync(Guid merchantId, Guid orderId, string actionId,
        CancellationToken ct = default)
    {
        db.WorkflowActions.TryGetValue(Key(merchantId, orderId, actionId), out var action);
        return Task.FromResult(action);
    }

    private static string Key(Guid merchantId, Guid orderId, string actionId) =>
        $"{merchantId:N}:{orderId:N}:{actionId}";
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
