using MoynaPay.Domain.Common;

namespace MoynaPay.Domain.Orders;

// Schema: orders

public enum OrderStatus
{
    /// <summary>Arrived from the shop. Nothing has been done to it yet.</summary>
    Received = 0,

    /// <summary>A confirmation call is out, or queued.</summary>
    Calling = 1,

    /// <summary>The customer said yes - by keypress, or through a person.</summary>
    Confirmed = 2,

    /// <summary>The customer said no. Reversible by a human, never by the machine.</summary>
    Rejected = 3,

    /// <summary>
    /// The machine could not get an answer it is allowed to act on. NOT a cancellation:
    /// the order stands and a person decides. Treating this as "cancelled" would delete
    /// real sales every day, which is why it is its own status and not a flag on Rejected.
    /// </summary>
    NeedsHuman = 4,

    AwaitingPayment = 5,
    Paid = 6,

    /// <summary>A consignment exists at the courier.</summary>
    Booked = 7,
    Shipped = 8,
    Delivered = 9,

    Cancelled = 10,
}

/// <summary>Who moved the order. The machine is not allowed everywhere a person is.</summary>
public enum Actor
{
    /// <summary>The pipeline, a worker, a webhook from an engine.</summary>
    Machine = 0,

    /// <summary>The merchant, or their staff, in the app.</summary>
    Merchant = 1,

    /// <summary>The merchant's own shop software, over the signed API.</summary>
    Shop = 2,
}

public class Order : BaseEntity
{
    /// <summary>
    /// The shop's own identifier. Opaque to us, unique per merchant, and what every answer
    /// is addressed by - so a shop never has to store an id of ours to find its own record.
    /// </summary>
    public string Reference { get; set; } = default!;

    public string CustomerName { get; set; } = default!;
    public string Msisdn { get; set; } = default!;
    public string? Address { get; set; }
    public string? Summary { get; set; }

    /// <summary>What the shop says the order is worth.</summary>
    public decimal Amount { get; set; }

    /// <summary>
    /// What the customer is actually asked to send. Set by the payment module, which may
    /// add a taka or two so the incoming figure is unambiguous on the wallet - never
    /// subtract. Show this, not Amount, or the customer sends the wrong number.
    /// </summary>
    public decimal? ChargedAmount { get; set; }

    public string Currency { get; set; } = "BDT";

    /// <summary>Anything else the shop wants read out or kept, as JSON.</summary>
    public string? FieldsJson { get; set; }

    public OrderStatus Status { get; set; } = OrderStatus.Received;

    /// <summary>Which key the customer pressed, when one was pressed.</summary>
    public string? Digit { get; set; }

    /// <summary>Why the machine gave up, or why a person decided as they did.</summary>
    public string? Reason { get; set; }

    public int CallAttempts { get; set; }
    public decimal? PaidAmount { get; set; }
    public string? TrxId { get; set; }
    public string? Courier { get; set; }
    public string? TrackingCode { get; set; }

    /// <summary>Overrides the merchant's default endpoint for this one order.</summary>
    public string? CallbackUrl { get; set; }

    public DateTimeOffset? ConfirmedAt { get; set; }
    public DateTimeOffset? PaidAt { get; set; }
    public DateTimeOffset? ShippedAt { get; set; }
    public DateTimeOffset? ClosedAt { get; set; }

    /// <summary>
    /// Who is on this order right now, so two staff do not ring the same customer within a
    /// minute of each other. Expires on its own - a claim that outlives the person who
    /// took it is worse than no claim, because nobody else will touch the order.
    /// </summary>
    public string? ClaimedBy { get; set; }
    public DateTimeOffset? ClaimedUntil { get; set; }

    public bool IsClaimed(DateTimeOffset now) => ClaimedUntil is { } until && until > now;
}

/// <summary>
/// Append-only. Every move, who made it, and what it said.
///
/// This is what the order timeline in the app reads, and it is the only honest answer to
/// "what happened to my order" - a status field alone cannot say what it used to be.
/// Nothing here is ever updated or deleted.
/// </summary>
public class OrderEvent : BaseEntity
{
    public Guid OrderId { get; set; }

    /// <summary>"order.received", "call.answered", "order.confirmed", …</summary>
    public string Type { get; set; } = default!;

    public Actor Actor { get; set; }

    /// <summary>The app's own user, when a person did it.</summary>
    public string? ActorName { get; set; }

    public OrderStatus? From { get; set; }
    public OrderStatus? To { get; set; }

    public string? Detail { get; set; }
    public string? PayloadJson { get; set; }

    public DateTimeOffset At { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// A result waiting to be delivered to the merchant's shop.
///
/// Written in the same transaction as the state change, so a notification cannot exist
/// without the change that caused it, nor the change without the notification. A worker
/// drains it; at-least-once, never exactly-once.
/// </summary>
public class OutboxMessage : BaseEntity
{
    public Guid OrderId { get; set; }
    public string EventType { get; set; } = default!;
    public string PayloadJson { get; set; } = default!;

    public int Attempts { get; set; }
    public DateTimeOffset NextAttemptAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? DeliveredAt { get; set; }
    public string? LastFailureReason { get; set; }
}
