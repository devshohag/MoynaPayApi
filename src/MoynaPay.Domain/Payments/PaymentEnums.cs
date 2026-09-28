namespace MoynaPay.Domain.Payments;

public enum PaymentMethod
{
    Bkash = 1,
    Nagad = 2,
    Rocket = 3,
    Upay = 4,
}

/// <summary>
/// Which kind of wallet the money lands in. Approved business accounts are the
/// primary rail; an ordinary personal account is a merchant's own informed choice
/// and carries provider terms-of-service risk.
/// </summary>
public enum WalletAccountType
{
    /// <summary>Provider-approved retail/business collection account.</summary>
    PersonalRetail = 1,
    Merchant = 2,
    Agent = 3,
    Personal = 4,
}

public enum InvoiceStatus
{
    Created = 0,
    AwaitingPayment = 1,
    /// <summary>Money arrived but the amount is short of the invoice.</summary>
    Partial = 2,
    /// <summary>Window elapsed without a match. Held, never failed outright, because
    /// operator messages and device backlogs arrive late.</summary>
    PendingReview = 3,
    Paid = 4,
    Expired = 5,
    Cancelled = 6,
    /// <summary>Reconciled against the merchant's statement.</summary>
    Settled = 7,
}

public enum SessionState
{
    Open = 1,
    Matched = 2,
    Expired = 3,
    Cancelled = 4,
}

public enum MatchStrategy
{
    /// <summary>Customer supplied the transaction id. Primary strategy through pilot.</summary>
    TrxId = 1,
    /// <summary>Amount reserved uniquely per wallet per window. Zero customer input.</summary>
    UniqueAmount = 2,
    /// <summary>An operator matched it by hand from the unmatched queue.</summary>
    Manual = 3,
}

public enum EventSource
{
    Notification = 1,
    Sms = 2,
}

public enum RawEventState
{
    Received = 0,
    Parsed = 1,
    /// <summary>No active template matched. Goes to the ops queue, never dropped.</summary>
    Unparseable = 2,
    /// <summary>Parsed and deliberately ignored - cash out, recharge, promo, OTP.</summary>
    Ignored = 3,

    /// <summary>
    /// Taken by a worker and not yet finished. A crash leaves rows here, so the pipeline
    /// releases anything claimed more than a few minutes ago before each run: a message
    /// that stalls visibly and reappears beats one that is silently held forever.
    /// </summary>
    Claimed = 4,
}

public enum DevicePermissionState
{
    Unknown = 0,
    Healthy = 1,
    NotificationAccessRevoked = 2,
    BatteryRestricted = 3,
}

public enum WebhookEndpointState
{
    /// <summary>Never checked. The dispatcher must not post to it.</summary>
    Unvalidated = 0,
    Valid = 1,
    /// <summary>Failed the outbound URL guard - private address, bad scheme, bad redirect.</summary>
    Rejected = 2,
}

public enum OutboxStatus
{
    Pending = 0,
    Dispatched = 1,
    Failed = 2,
    DeadLettered = 3,
}

public enum DeliveryStatus
{
    Pending = 0,
    Succeeded = 1,
    Failed = 2,
    DeadLettered = 3,
}

public enum SubscriptionStatus
{
    Trialing = 0,
    Active = 1,
    PastDue = 2,
    Cancelled = 3,
}

public enum PlanCode
{
    Trial = 0,
    Starter = 1,
    Business = 2,
    Agency = 3,
}

public enum FraudSignalReason
{
    DuplicateTrxId = 1,
    VelocityAnomaly = 2,
    ManualFlag = 3,
}
