using System.Text.Json;
using MoynaPay.Application.Abstractions;
using MoynaPay.Domain.Merchants;
using MoynaPay.Domain.Orders;

namespace MoynaPay.Application.Orders;

public sealed record CreateOrderCommand
{
    public required Guid MerchantId { get; init; }
    public required string Reference { get; init; }
    public required string Msisdn { get; init; }
    public string? CustomerName { get; init; }
    public decimal Amount { get; init; }
    public string? Summary { get; init; }
    public string? Address { get; init; }
    public IReadOnlyDictionary<string, string>? Fields { get; init; }
    public string? CallbackUrl { get; init; }
}

public enum CreateOrderOutcome
{
    Created = 0,

    /// <summary>The reference already existed. The order that was there comes back.</summary>
    AlreadyExists = 1,

    Invalid = 2,
    NoMerchant = 3,
}

public sealed record CreateOrderResult(CreateOrderOutcome Outcome, Order? Order, string? Reason)
{
    public bool Succeeded => Outcome is CreateOrderOutcome.Created or CreateOrderOutcome.AlreadyExists;
}

/// <summary>
/// Takes an order in from a shop.
///
/// Idempotent on (merchant, reference), and that is the single most important property
/// here. A shop's HTTP call can time out after we committed; from their side that is
/// indistinguishable from failing before. If a retry made a second order, the customer
/// would be rung twice and could be charged twice - so a repeat returns what already
/// exists and nothing else happens.
/// </summary>
public sealed class CreateOrderService(
    IOrderStore orders, IMerchantStore merchants, IClock clock)
{
    public async Task<CreateOrderResult> CreateAsync(
        CreateOrderCommand command, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (string.IsNullOrWhiteSpace(command.Reference))
        {
            return new CreateOrderResult(CreateOrderOutcome.Invalid, null, "reference is required");
        }

        var normalised = Msisdn.Normalise(command.Msisdn);
        if (normalised is null)
        {
            // Refused at the door rather than after three failed calls. The shop can fix a
            // number today; nobody can fix it after the customer has been rung.
            return new CreateOrderResult(
                CreateOrderOutcome.Invalid, null,
                $"'{command.Msisdn}' is not a Bangladeshi mobile number");
        }

        if (command.Amount < 0m)
        {
            return new CreateOrderResult(CreateOrderOutcome.Invalid, null, "amount cannot be negative");
        }

        var merchant = await merchants.FindAsync(command.MerchantId, ct).ConfigureAwait(false);
        if (merchant is null)
        {
            return new CreateOrderResult(CreateOrderOutcome.NoMerchant, null, "unknown merchant");
        }

        var existing = await orders
            .FindByReferenceAsync(command.MerchantId, command.Reference, ct).ConfigureAwait(false);

        if (existing is not null)
        {
            return new CreateOrderResult(CreateOrderOutcome.AlreadyExists, existing, null);
        }

        var subscription = await merchants
            .SubscriptionAsync(command.MerchantId, ct).ConfigureAwait(false);

        var now = clock.UtcNow;

        var order = new Order
        {
            TenantId = command.MerchantId,
            Reference = command.Reference.Trim(),
            CustomerName = (command.CustomerName ?? "").Trim(),
            Msisdn = normalised,
            Amount = command.Amount,
            Summary = command.Summary,
            Address = command.Address,
            CallbackUrl = command.CallbackUrl,
            FieldsJson = command.Fields is { Count: > 0 }
                ? JsonSerializer.Serialize(command.Fields)
                : null,
            Status = OrderLifecycle.FirstStep(subscription),
            CreatedAt = now,
            UpdatedAt = now,
        };

        // A merchant who bought nothing still gets a usable record rather than an order
        // stuck at Received forever with no worker that will ever look at it.
        if (order.Status == OrderStatus.Confirmed) order.ConfirmedAt = now;

        var first = new OrderEvent
        {
            TenantId = command.MerchantId,
            OrderId = order.Id,
            Type = "order.received",
            Actor = Actor.Shop,
            From = null,
            To = order.Status,
            Detail = Describe(subscription),
            At = now,
        };

        await orders.SaveNewAsync(order, first, ct).ConfigureAwait(false);

        return new CreateOrderResult(CreateOrderOutcome.Created, order, null);
    }

    private static string Describe(Subscription s)
    {
        var on = new List<string>(3);
        if (s.Calls) on.Add("calls");
        if (s.Payments) on.Add("payments");
        if (s.Courier) on.Add("courier");

        return on.Count == 0 ? "no services enabled" : string.Join(" + ", on);
    }
}

public sealed record TransitionCommand
{
    public required Guid MerchantId { get; init; }
    public required Guid OrderId { get; init; }
    public required OrderStatus To { get; init; }
    public required Actor By { get; init; }

    public string? ActorName { get; init; }
    public string? Reason { get; init; }
    public string? Digit { get; init; }
    public string EventType { get; init; } = "order.changed";
}

public enum TransitionOutcome
{
    Moved = 0,

    /// <summary>Already there. Not an error - a retry, and it does nothing.</summary>
    Unchanged = 1,

    Refused = 2,
    NotFound = 3,
}

public sealed record TransitionResult(TransitionOutcome Outcome, Order? Order, string? Reason);

/// <summary>
/// Moves an order, writes down who moved it, and queues the news for the shop.
///
/// Every state change in the product goes through here - the call engine's webhook, the
/// payment matcher, a merchant tapping a button in the app. One door means one place
/// where the rules are enforced and one place that cannot forget the audit row.
/// </summary>
public sealed class OrderTransitionService(
    IOrderStore orders, IMerchantStore merchants, IClock clock)
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };

    public async Task<TransitionResult> ApplyAsync(
        TransitionCommand command, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        var order = await orders
            .FindByIdAsync(command.MerchantId, command.OrderId, ct).ConfigureAwait(false);

        if (order is null) return new TransitionResult(TransitionOutcome.NotFound, null, null);

        // Asking for the state it is already in is what a retry looks like. It is not an
        // error and it must not write a second event or send a second notification.
        if (order.Status == command.To)
        {
            return new TransitionResult(TransitionOutcome.Unchanged, order, null);
        }

        if (OrderLifecycle.Refuse(order.Status, command.To, command.By) is { } refusal)
        {
            return new TransitionResult(TransitionOutcome.Refused, order, refusal);
        }

        var now = clock.UtcNow;
        var from = order.Status;

        order.Status = command.To;
        order.UpdatedAt = now;

        if (command.Reason is not null) order.Reason = command.Reason;
        if (command.Digit is not null) order.Digit = command.Digit;

        switch (command.To)
        {
            case OrderStatus.Confirmed: order.ConfirmedAt ??= now; break;
            case OrderStatus.Paid: order.PaidAt ??= now; break;
            case OrderStatus.Shipped: order.ShippedAt ??= now; break;
            case OrderStatus.Delivered:
            case OrderStatus.Cancelled: order.ClosedAt ??= now; break;
        }

        // A claim is about who is working on it now. Once it has moved, it is nobody's.
        order.ClaimedBy = null;
        order.ClaimedUntil = null;

        var change = new OrderEvent
        {
            TenantId = command.MerchantId,
            OrderId = order.Id,
            Type = command.EventType,
            Actor = command.By,
            ActorName = command.ActorName,
            From = from,
            To = command.To,
            Detail = command.Reason,
            At = now,
        };

        var notify = await BuildNotificationAsync(order, command, now, ct).ConfigureAwait(false);

        await orders.SaveChangeAsync(order, change, notify, ct).ConfigureAwait(false);

        return new TransitionResult(TransitionOutcome.Moved, order, null);
    }

    /// <summary>
    /// The outbox row, when this is a change the shop asked to hear about.
    ///
    /// Intermediate steps are not sent. A shop does not want to be told an order started
    /// ringing; it wants to be told what happened. Sending everything trains people to
    /// ignore the webhook, which is worse than sending too little.
    /// </summary>
    private async Task<OutboxMessage?> BuildNotificationAsync(
        Order order, TransitionCommand command, DateTimeOffset now, CancellationToken ct)
    {
        var name = command.To switch
        {
            OrderStatus.Confirmed => "order.confirmed",
            OrderStatus.Rejected => "order.rejected",
            OrderStatus.NeedsHuman => "order.needs_human",
            OrderStatus.Paid => "order.paid",
            OrderStatus.Shipped => "order.shipped",
            OrderStatus.Cancelled => "order.cancelled",
            _ => null,
        };

        if (name is null) return null;

        var endpoint = await merchants.WebhookAsync(command.MerchantId, ct).ConfigureAwait(false);

        // No endpoint, or the merchant turned it off, and no per-order override: nobody is
        // listening, so queuing the message would only fill a table nothing drains.
        if (order.CallbackUrl is null && (endpoint is null || !endpoint.Active)) return null;

        var payload = JsonSerializer.Serialize(new
        {
            @event = name,
            reference = order.Reference,
            status = order.Status,
            amount = order.Amount,
            chargedAmount = order.ChargedAmount,
            paidAmount = order.PaidAmount,
            digit = order.Digit,
            reason = order.Reason,
            trxId = order.TrxId,
            trackingCode = order.TrackingCode,
            attempts = order.CallAttempts,
            at = now,
        }, Json);

        return new OutboxMessage
        {
            TenantId = command.MerchantId,
            OrderId = order.Id,
            EventType = name,
            PayloadJson = payload,
            NextAttemptAt = now,
            CreatedAt = now,
            UpdatedAt = now,
        };
    }
}
