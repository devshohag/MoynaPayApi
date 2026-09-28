using MoynaPay.Domain.Merchants;

namespace MoynaPay.Domain.Orders;

/// <summary>
/// Which moves an order may make, and who may make them.
///
/// This is the centre of the product. Every other part - the call engine, the payment
/// matcher, the courier, the app - asks this class whether a move is allowed, so there is
/// one answer rather than one per caller. A transition that is legal in the worker and
/// illegal in the API is a bug nobody finds until a merchant loses money.
///
/// Two rules are not negotiable and are the reason this is a table rather than a pile of
/// if-statements:
///
///   The machine may never reject an order. Silence, a wrong key, three unanswered calls -
///   none of those is a customer saying no. They go to NeedsHuman, and a person decides.
///
///   A rejection can be undone. A thumb lands on 0 by mistake often enough that a
///   machine-recorded "no" has to stay recoverable by the merchant - but only by them.
/// </summary>
public static class OrderLifecycle
{
    private readonly record struct Move(OrderStatus From, OrderStatus To, Actor By);

    private static readonly HashSet<Move> Allowed = BuildAllowed();

    private static HashSet<Move> BuildAllowed()
    {
        var moves = new HashSet<Move>();

        void Add(OrderStatus from, OrderStatus to, params Actor[] actors)
        {
            foreach (var actor in actors) moves.Add(new Move(from, to, actor));
        }

        var anyone = new[] { Actor.Machine, Actor.Merchant, Actor.Shop };

        // Starting the pipeline.
        Add(OrderStatus.Received, OrderStatus.Calling, Actor.Machine);
        Add(OrderStatus.Received, OrderStatus.AwaitingPayment, Actor.Machine);
        Add(OrderStatus.Received, OrderStatus.Confirmed, Actor.Machine, Actor.Merchant);

        // The call. Confirmed and NeedsHuman are the machine's only outcomes; Rejected is
        // reached by a keypress, which the call engine reports as a customer decision -
        // see DecideFromCall.
        Add(OrderStatus.Calling, OrderStatus.Confirmed, Actor.Machine, Actor.Merchant);
        Add(OrderStatus.Calling, OrderStatus.Rejected, Actor.Machine, Actor.Merchant);
        Add(OrderStatus.Calling, OrderStatus.NeedsHuman, Actor.Machine);
        Add(OrderStatus.Calling, OrderStatus.Calling, Actor.Machine, Actor.Merchant);

        // A person clearing the queue.
        Add(OrderStatus.NeedsHuman, OrderStatus.Confirmed, Actor.Merchant);
        Add(OrderStatus.NeedsHuman, OrderStatus.Rejected, Actor.Merchant);
        Add(OrderStatus.NeedsHuman, OrderStatus.Calling, Actor.Merchant);

        // Undoing a mis-press. Only the merchant, and only back to Confirmed.
        Add(OrderStatus.Rejected, OrderStatus.Confirmed, Actor.Merchant);
        Add(OrderStatus.Rejected, OrderStatus.Calling, Actor.Merchant);

        // Money.
        Add(OrderStatus.Confirmed, OrderStatus.AwaitingPayment, Actor.Machine);
        Add(OrderStatus.Confirmed, OrderStatus.Booked, Actor.Machine, Actor.Merchant);
        Add(OrderStatus.AwaitingPayment, OrderStatus.Paid, Actor.Machine);

        // Shipping.
        Add(OrderStatus.Paid, OrderStatus.Booked, Actor.Machine, Actor.Merchant);
        Add(OrderStatus.Booked, OrderStatus.Shipped, Actor.Machine, Actor.Merchant);
        Add(OrderStatus.Shipped, OrderStatus.Delivered, Actor.Machine, Actor.Merchant);

        // Cancelling, from anywhere the goods have not left.
        foreach (var from in new[]
        {
            OrderStatus.Received, OrderStatus.Calling, OrderStatus.Confirmed,
            OrderStatus.Rejected, OrderStatus.NeedsHuman, OrderStatus.AwaitingPayment,
            OrderStatus.Paid, OrderStatus.Booked,
        })
        {
            Add(from, OrderStatus.Cancelled, anyone);
        }

        return moves;
    }

    public static bool CanMove(OrderStatus from, OrderStatus to, Actor by) =>
        Allowed.Contains(new Move(from, to, by));

    /// <summary>
    /// Why a move was refused, in words a caller can show. Null when it is allowed.
    ///
    /// Written as sentences because these reach a merchant through the API, and
    /// "transition not permitted" sends them to support while "the order has already been
    /// shipped" does not.
    /// </summary>
    public static string? Refuse(OrderStatus from, OrderStatus to, Actor by)
    {
        if (CanMove(from, to, by)) return null;

        if (from == to) return $"The order is already {from}.";

        if (IsClosed(from))
        {
            return from == OrderStatus.Cancelled
                ? "The order was cancelled."
                : "The order has already been delivered.";
        }

        if (to == OrderStatus.Cancelled && from is OrderStatus.Shipped or OrderStatus.Delivered)
        {
            return "The order has already been shipped and cannot be cancelled here.";
        }

        if (to == OrderStatus.Rejected && by == Actor.Machine && from == OrderStatus.NeedsHuman)
        {
            return "Only a person can reject an order that was sent for review.";
        }

        if (from == OrderStatus.Rejected && by != Actor.Merchant)
        {
            return "Only the merchant can reopen a rejected order.";
        }

        return $"An order that is {from} cannot become {to}.";
    }

    public static bool IsClosed(OrderStatus status) =>
        status is OrderStatus.Delivered or OrderStatus.Cancelled;

    /// <summary>
    /// True while the order is still waiting on somebody or something. Used by the app's
    /// counters and by the worker that looks for work.
    /// </summary>
    public static bool IsOpen(OrderStatus status) => !IsClosed(status);

    /// <summary>
    /// Where a newly received order goes, given what the merchant actually bought.
    ///
    /// The skip is decided here rather than by the caller, so a merchant who never bought
    /// the call service cannot be rung by a code path that forgot to check. A merchant
    /// with nothing at all still gets a usable record: the order is simply confirmed, and
    /// the shop is told so.
    /// </summary>
    public static OrderStatus FirstStep(Subscription subscription)
    {
        ArgumentNullException.ThrowIfNull(subscription);

        if (subscription.Calls) return OrderStatus.Calling;
        if (subscription.Payments) return OrderStatus.AwaitingPayment;

        return OrderStatus.Confirmed;
    }

    /// <summary>
    /// What follows a confirmation. Same reasoning as FirstStep: the pipeline's shape is a
    /// property of the subscription, not of whoever is writing the next handler.
    /// </summary>
    public static OrderStatus? AfterConfirmed(Subscription subscription)
    {
        ArgumentNullException.ThrowIfNull(subscription);

        if (subscription.Payments) return OrderStatus.AwaitingPayment;
        if (subscription.Courier) return OrderStatus.Booked;

        return null;
    }

    public static OrderStatus? AfterPaid(Subscription subscription)
    {
        ArgumentNullException.ThrowIfNull(subscription);

        return subscription.Courier ? OrderStatus.Booked : null;
    }

    /// <summary>
    /// Turns what happened on a call into a status.
    ///
    /// The outcome is the caller's own word - the call engine reports what it heard, not
    /// what it concluded - and this is the single place that decides what it means. A
    /// keypress of 0 is a customer rejecting the order, which is why Rejected is reachable
    /// from a machine-reported outcome while the machine may not choose it on its own.
    /// </summary>
    public static OrderStatus FromCallOutcome(CallOutcome outcome) => outcome switch
    {
        CallOutcome.Confirmed => OrderStatus.Confirmed,
        CallOutcome.Rejected => OrderStatus.Rejected,

        // Everything else is an absence of an answer, not an answer. A person rings back.
        _ => OrderStatus.NeedsHuman,
    };
}

/// <summary>
/// What a call ended as, reported by the call engine.
///
/// Deliberately wider than "yes or no": nobody home, a wrong number and a customer asking
/// for a person are different facts, and flattening them loses the difference between an
/// order to chase and one to leave alone.
/// </summary>
public enum CallOutcome
{
    Confirmed = 0,
    Rejected = 1,
    NeedsHuman = 2,
    NoAnswer = 3,
    Unreachable = 4,
    Failed = 5,
}
