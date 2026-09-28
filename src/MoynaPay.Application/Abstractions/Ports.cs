using MoynaPay.Domain.Merchants;
using MoynaPay.Domain.Orders;

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

    Task SaveMerchantAsync(Merchant merchant, Subscription subscription, CancellationToken ct = default);

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

    Task<IReadOnlyList<Order>> ListAsync(Guid merchantId, OrderQuery query,
        CancellationToken ct = default);

    Task<IReadOnlyList<OrderEvent>> TimelineAsync(Guid merchantId, Guid orderId,
        CancellationToken ct = default);
}

public sealed record OrderQuery
{
    public OrderStatus? Status { get; init; }

    /// <summary>The review queue: everything a person still has to decide.</summary>
    public bool NeedsHumanOnly { get; init; }

    public string? Search { get; init; }
    public int Take { get; init; } = 50;
    public DateTimeOffset? Before { get; init; }
}
