using MoynaPay.Domain.Merchants;
using MoynaPay.Domain.Orders;
using MoynaPay.Domain.Payments;

namespace MoynaPay.Application.Abstractions;

/// <summary>
/// Time, as a dependency.
///
/// Not ceremony: claims expire, calls are only placed inside a window, and signatures are
/// refused when the clock is out. None of that can be tested against DateTimeOffset.UtcNow.
/// </summary>
public interface IClock
{
    DateTimeOffset UtcNow { get; }
}

public sealed class SystemClock : IClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}

/// <summary>
/// Seals a secret at rest and opens it again.
///
/// The API secret cannot be hashed - the server has to recompute the merchant's HMAC - so
/// it is encrypted instead, with a key ring that lives outside the database. A database
/// backup that leaks is then a pile of ciphertext rather than every merchant's signing key.
/// </summary>
public interface ISecretProtector
{
    string Protect(string plaintext, out string keyRingId);

    string Unprotect(string cipher, string keyRingId);
}

/// <summary>
/// Remembers the nonces already seen, so a captured request cannot be replayed.
///
/// Only needs to remember as far back as the timestamp tolerance: anything older is
/// already refused by the clock, so keeping it buys nothing and costs memory.
/// </summary>
public interface INonceStore
{
    /// <summary>False when this nonce has been used before.</summary>
    Task<bool> TryUseAsync(string keyId, string nonce, CancellationToken ct = default);
}

public interface IMerchantStore
{
    Task<Merchant?> FindAsync(Guid merchantId, CancellationToken ct = default);

    Task<Merchant?> FindByMsisdnAsync(string msisdn, CancellationToken ct = default);

    Task SaveMerchantAsync(Merchant merchant, Subscription subscription, CancellationToken ct = default);

    Task SaveMerchantProfileAsync(Merchant merchant, CancellationToken ct = default);

    Task<Subscription> SubscriptionAsync(Guid merchantId, CancellationToken ct = default);

    /// <summary>Resolves a signing key to the merchant it belongs to. Null when revoked.</summary>
    Task<ApiCredential?> FindCredentialAsync(string keyId, CancellationToken ct = default);

    Task<IReadOnlyList<ApiCredential>> ListCredentialsAsync(Guid merchantId, CancellationToken ct = default);

    Task<bool> HasAnyCredentialAsync(Guid merchantId, CancellationToken ct = default);

    Task SaveCredentialAsync(ApiCredential credential, CancellationToken ct = default);

    Task<bool> RevokeCredentialAsync(Guid merchantId, string keyId, DateTimeOffset at,
        CancellationToken ct = default);

    Task<WebhookEndpoint?> WebhookAsync(Guid merchantId, CancellationToken ct = default);

    Task SaveWebhookAsync(WebhookEndpoint endpoint, CancellationToken ct = default);

    Task UpdateWebhookDeliveryAsync(Guid merchantId, DateTimeOffset? deliveredAt,
        string? failureReason, CancellationToken ct = default);
}

public sealed record WebhookSendResult(bool Succeeded, int? StatusCode, string? FailureReason);

public interface IWebhookSender
{
    Task<WebhookSendResult> SendAsync(WebhookEndpoint endpoint, string body, string signature,
        CancellationToken ct = default);
}

public sealed class AppOtpChallenge
{
    public required Guid Id { get; init; }
    public required Guid MerchantId { get; init; }
    public required string Msisdn { get; init; }
    public required string CodeHash { get; init; }
    public int FailedAttempts { get; set; }
    public required DateTimeOffset ExpiresAt { get; init; }
    public DateTimeOffset? ConsumedAt { get; set; }
    public required DateTimeOffset CreatedAt { get; init; }
}

public sealed class AppToken
{
    public required Guid Id { get; init; }
    public required Guid MerchantId { get; init; }
    public required string TokenHash { get; init; }
    public required AppTokenKind Kind { get; init; }
    public required DateTimeOffset ExpiresAt { get; init; }
    public DateTimeOffset? RevokedAt { get; set; }
    public string? ReplacedByHash { get; set; }
    public required DateTimeOffset CreatedAt { get; init; }

    public bool IsUsable(DateTimeOffset now) => RevokedAt is null && ExpiresAt > now;
}

public enum AppTokenKind
{
    Access,
    Refresh,
}

public interface IAppAuthStore
{
    Task SaveOtpAsync(AppOtpChallenge challenge, CancellationToken ct = default);

    Task<AppOtpChallenge?> LatestOtpAsync(string msisdn, CancellationToken ct = default);

    Task SaveTokenAsync(AppToken token, CancellationToken ct = default);

    Task<AppToken?> FindTokenAsync(string tokenHash, AppTokenKind kind, CancellationToken ct = default);

    Task RevokeTokenAsync(string tokenHash, AppTokenKind kind, DateTimeOffset now,
        string? replacedByHash = null, CancellationToken ct = default);
}

public interface IRateLimitStore
{
    Task<bool> TryConsumeAsync(string key, DateTimeOffset now, TimeSpan window, int limit,
        CancellationToken ct = default);
}

public sealed class WorkflowSession
{
    public required Guid MerchantId { get; init; }
    public required Guid OrderId { get; init; }
    public required string Name { get; init; }
    public string Step { get; set; } = "received";
    public bool Complete { get; set; }
    public string? LeaseOwner { get; set; }
    public DateTimeOffset? LeaseUntil { get; set; }
    public List<string> History { get; } = [];
    public DateTimeOffset UpdatedAt { get; set; }
}

public interface IWorkflowSessionStore
{
    Task<WorkflowSession?> FindAsync(Guid merchantId, Guid orderId, string name,
        CancellationToken ct = default);

    Task SaveAsync(WorkflowSession session, CancellationToken ct = default);

    Task<WorkflowSession?> TryLeaseNextAsync(string name, string runnerId, DateTimeOffset now,
        TimeSpan leaseFor, CancellationToken ct = default);

    Task ReleaseAsync(WorkflowSession session, string runnerId, CancellationToken ct = default);
}

public enum WorkflowActionStatus
{
    Started,
    Completed,
}

public sealed class WorkflowAction
{
    public required Guid MerchantId { get; init; }
    public required Guid OrderId { get; init; }
    public required string ActionId { get; init; }
    public WorkflowActionStatus Status { get; set; } = WorkflowActionStatus.Started;
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public string? Result { get; set; }
}

public enum WorkflowActionStart
{
    Started,
    AlreadyCompleted,
    AlreadyRunning,
}

public interface IWorkflowActionStore
{
    Task<WorkflowActionStart> TryStartAsync(Guid merchantId, Guid orderId, string actionId,
        DateTimeOffset now, CancellationToken ct = default);

    Task CompleteAsync(Guid merchantId, Guid orderId, string actionId, string? result,
        DateTimeOffset now, CancellationToken ct = default);

    Task<WorkflowAction?> FindAsync(Guid merchantId, Guid orderId, string actionId,
        CancellationToken ct = default);
}

public interface IOrderStore
{
    Task<Order?> FindByReferenceAsync(Guid merchantId, string reference, CancellationToken ct = default);

    Task<Order?> FindByIdAsync(Guid merchantId, Guid orderId, CancellationToken ct = default);

    /// <summary>
    /// Writes the order, its first event and any outbox rows as one unit.
    ///
    /// One call rather than three, because an order that exists without the event that
    /// explains it - or a notification that exists without the change that caused it - is
    /// a state nothing downstream can make sense of.
    /// </summary>
    Task SaveNewAsync(Order order, OrderEvent first, CancellationToken ct = default);

    Task SaveChangeAsync(Order order, OrderEvent change, OutboxMessage? notify,
        CancellationToken ct = default);

    Task SaveAuditAsync(Order order, OrderEvent audit, CancellationToken ct = default);

    Task<ReviewClaimStoreResult> TryClaimReviewAsync(Guid merchantId, Guid orderId,
        string reviewer, DateTimeOffset now, TimeSpan claimFor, CancellationToken ct = default);

    Task<ReviewReleaseStoreResult> ReleaseReviewClaimAsync(Guid merchantId, Guid orderId,
        string reviewer, DateTimeOffset now, CancellationToken ct = default);

    Task<IReadOnlyList<Order>> ListAsync(Guid merchantId, OrderQuery query,
        CancellationToken ct = default);

    Task<IReadOnlyDictionary<OrderStatus, int>> CountByStatusAsync(Guid merchantId,
        CancellationToken ct = default);

    Task<HomeMetrics> HomeMetricsAsync(Guid merchantId, DateTimeOffset todayStart,
        DateTimeOffset sevenDayStart, DateTimeOffset now, CancellationToken ct = default);

    Task<IReadOnlyList<OrderEvent>> TimelineAsync(Guid merchantId, Guid orderId,
        CancellationToken ct = default);
}

public sealed record HomeMetricWindow(
    int New,
    int Confirmed,
    int NeedsHuman,
    int Paid,
    int Shipped,
    int FailedCalls);

public sealed record HomeMetrics(
    DateTimeOffset TodayStart,
    DateTimeOffset SevenDayStart,
    DateTimeOffset AsOf,
    HomeMetricWindow Today,
    HomeMetricWindow SevenDays);

public interface IInvoiceStore
{
    Task<Invoice?> FindByOrderRefAsync(Guid merchantId, string orderRef, CancellationToken ct = default);

    Task SaveAsync(Invoice invoice, CancellationToken ct = default);
}

public sealed record OrderQuery
{
    public OrderStatus? Status { get; init; }

    /// <summary>The review queue: everything a person still has to decide.</summary>
    public bool NeedsHumanOnly { get; init; }

    public string? Search { get; init; }
    public int Take { get; init; } = 50;
    public DateTimeOffset? Before { get; init; }
    public DateTimeOffset? From { get; init; }
    public DateTimeOffset? To { get; init; }
}

public enum ReviewClaimStoreOutcome
{
    Claimed,
    NotFound,
    NotInReview,
    AlreadyClaimed,
}

public sealed record ReviewClaimStoreResult(
    ReviewClaimStoreOutcome Outcome, Order? Order, string? Reason);

public enum ReviewReleaseStoreOutcome
{
    Released,
    NotFound,
    NotClaimed,
    ClaimedByAnother,
}

public sealed record ReviewReleaseStoreResult(
    ReviewReleaseStoreOutcome Outcome, Order? Order, string? Reason);
