using MoynaPay.Domain.Common;

namespace MoynaPay.Domain.Merchants;

// Schema: merchants

public enum MerchantStatus
{
    Trial = 0,
    Active = 1,
    Suspended = 2,
    Cancelled = 3,
}

public class Merchant : BaseEntity
{
    /// <summary>What the customer hears on the call and reads on the receipt.</summary>
    public string Name { get; set; } = default!;

    /// <summary>The owner's number. Also how they log into the app.</summary>
    public string Msisdn { get; set; } = default!;

    public MerchantStatus Status { get; set; } = MerchantStatus.Trial;

    /// <summary>
    /// Stored even though every merchant today is in Dhaka. Calling hours are expressed in
    /// the merchant's own time, and a service that hard-codes one zone has to be unpicked
    /// the first time somebody sells across a border.
    /// </summary>
    public string TimeZone { get; set; } = "Asia/Dhaka";

    public string? Address { get; set; }
    public string? SupportMsisdn { get; set; }
}

/// <summary>
/// Which of the three services this merchant bought.
///
/// Not a set of feature flags scattered through the code - one row, read by the pipeline,
/// so "what happens to an order here" has exactly one answer and it is visible in the
/// database. A merchant who takes only payments must never be rung, and the way to
/// guarantee that is to make the skip a property of the data rather than an if-statement
/// somebody can forget.
/// </summary>
public class Subscription : BaseEntity
{
    public bool Calls { get; set; }
    public bool Payments { get; set; }
    public bool Courier { get; set; }

    public string Plan { get; set; } = "trial";

    public bool Has(ServiceKind kind) => kind switch
    {
        ServiceKind.Calls => Calls,
        ServiceKind.Payments => Payments,
        ServiceKind.Courier => Courier,
        _ => false,
    };
}

public enum ServiceKind
{
    Calls = 0,
    Payments = 1,
    Courier = 2,
}

/// <summary>
/// One signing key, belonging to one merchant's shop software.
///
/// The secret is kept ENCRYPTED, not hashed. A hash would be the safer habit, but the
/// server has to recompute the merchant's HMAC to verify a request, so it needs the
/// secret itself. It is sealed with the same key ring YoPay uses, and the plaintext is
/// returned exactly once - on the response that created it - and never again.
/// </summary>
public class ApiCredential : BaseEntity
{
    /// <summary>The public half, sent in X-MoynaPay-Key.</summary>
    public string KeyId { get; set; } = default!;

    /// <summary>Encrypted at rest. Never logged, never returned.</summary>
    public string SecretCipher { get; set; } = default!;

    /// <summary>Which key ring entry sealed it, so keys can be rotated without downtime.</summary>
    public string KeyRingId { get; set; } = default!;

    /// <summary>The merchant's own label: "my WooCommerce site".</summary>
    public string Label { get; set; } = default!;

    public DateTimeOffset? LastUsedAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }

    public bool IsActive => RevokedAt is null && !IsDeleted;
}

/// <summary>Where a merchant's results are posted.</summary>
public class WebhookEndpoint : BaseEntity
{
    public string Url { get; set; } = default!;

    /// <summary>Encrypted, like the API secret, and for the same reason.</summary>
    public string SecretCipher { get; set; } = default!;
    public string KeyRingId { get; set; } = default!;

    public bool Active { get; set; } = true;
    public string? LastFailureReason { get; set; }
    public DateTimeOffset? LastDeliveredAt { get; set; }
}
