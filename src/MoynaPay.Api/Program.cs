using System.Text.Json;
using System.Text.Json.Serialization;
using MoynaPay.Api;
using MoynaPay.Application.Abstractions;
using MoynaPay.Application.Orders;
using MoynaPay.Domain.Merchants;
using MoynaPay.Domain.Orders;
using MoynaPay.Infrastructure;
using MoynaPay.Infrastructure.Memory;

// MoynaPay - phase 1.
//
// Orders arrive from a shop, move through their states, and the result goes back out.
// No calls, no money, no courier yet: those hang off the order, and the order has to be
// right first.

var builder = WebApplication.CreateBuilder(args);

// Enums go out as names, never numbers. 4 is Confirmed today; insert a status and 4 is
// something else for every shop already in production. Names do not renumber themselves.
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
});

// Stores, clock and secret protection all come from one place, chosen by configuration.
// The host does not know or care which it got.
builder.Services.AddMoynaPay(builder.Configuration);

builder.Services.AddScoped<CreateOrderService>();
builder.Services.AddScoped<OrderTransitionService>();

var app = builder.Build();

// Said out loud, every start. A service holding its merchants in memory looks healthy
// right up until it restarts and every order is gone.
if (DependencyInjection.IsInMemory(app.Configuration))
{
    app.Logger.LogWarning(
        "In-memory stores. Orders do not survive a restart, and secrets are not encrypted. " +
        "Development only.");

    Seed(app.Services.GetRequiredService<MemoryDatabase>());
}

app.UseMiddleware<SignedRequestMiddleware>();

app.MapGet("/v1/health", () => Results.Ok(new { status = "ok", service = "MoynaPay" }));

// ---------------------------------------------------------------------------
// Orders, from the shop.
// ---------------------------------------------------------------------------
app.MapPost("/v1/orders", async (
    CreateOrderRequest request, HttpContext context, CreateOrderService service,
    CancellationToken ct) =>
{
    if (context.Items[SignedRequestMiddleware.MerchantItem] is not Guid merchantId)
    {
        return Results.Unauthorized();
    }

    var result = await service.CreateAsync(new CreateOrderCommand
    {
        MerchantId = merchantId,
        Reference = request.Reference ?? "",
        Msisdn = request.Msisdn ?? "",
        CustomerName = request.CustomerName,
        Amount = request.Amount,
        Summary = request.Summary,
        Address = request.Address,
        Fields = request.Fields,
        CallbackUrl = request.CallbackUrl,
    }, ct).ConfigureAwait(false);

    if (!result.Succeeded)
    {
        return Results.Problem(
            title: result.Reason ?? "The order could not be accepted.",
            statusCode: result.Outcome == CreateOrderOutcome.NoMerchant
                ? StatusCodes.Status403Forbidden
                : StatusCodes.Status400BadRequest);
    }

    var view = View(result.Order!);

    // A repeat answers 200 with the order that already exists, not 201. The shop's retry
    // after a timeout gets the same record it would have got the first time, which is the
    // whole point of making this idempotent.
    return result.Outcome == CreateOrderOutcome.Created
        ? Results.Created($"/v1/orders/{result.Order!.Reference}", view)
        : Results.Ok(view);
});

app.MapGet("/v1/orders/{reference}", async (
    string reference, HttpContext context, IOrderStore orders, CancellationToken ct) =>
{
    if (context.Items[SignedRequestMiddleware.MerchantItem] is not Guid merchantId)
    {
        return Results.Unauthorized();
    }

    var order = await orders.FindByReferenceAsync(merchantId, reference, ct).ConfigureAwait(false);

    return order is null ? Results.NotFound() : Results.Ok(View(order));
});

app.MapPost("/v1/orders/{reference}/cancel", async (
    string reference, CancelRequest? request, HttpContext context,
    IOrderStore orders, OrderTransitionService transitions, CancellationToken ct) =>
{
    if (context.Items[SignedRequestMiddleware.MerchantItem] is not Guid merchantId)
    {
        return Results.Unauthorized();
    }

    var order = await orders.FindByReferenceAsync(merchantId, reference, ct).ConfigureAwait(false);
    if (order is null) return Results.NotFound();

    var result = await transitions.ApplyAsync(new TransitionCommand
    {
        MerchantId = merchantId,
        OrderId = order.Id,
        To = OrderStatus.Cancelled,
        By = Actor.Shop,
        Reason = request?.Reason,
        EventType = "order.cancelled",
    }, ct).ConfigureAwait(false);

    return result.Outcome switch
    {
        TransitionOutcome.Moved or TransitionOutcome.Unchanged => Results.Ok(View(result.Order!)),
        TransitionOutcome.NotFound => Results.NotFound(),

        // 409 rather than 400: the request was well formed, the order is simply past the
        // point where this is possible. The sentence says which point.
        _ => Results.Problem(title: result.Reason, statusCode: StatusCodes.Status409Conflict),
    };
});

app.Run();

static object View(Order order) => new
{
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
    createdAt = order.CreatedAt,
    confirmedAt = order.ConfirmedAt,
};

/// <summary>
/// One merchant and one key, so the service can be driven the moment it starts.
///
/// Development only, and the values are deliberately obvious rather than realistic: a
/// seeded secret that looks like a real one is a secret somebody eventually ships.
/// </summary>
static void Seed(MemoryDatabase db)
{
    var merchantId = Guid.Parse("01929999-0000-7000-8000-000000000001");

    db.Merchants[merchantId] = new Merchant
    {
        Id = merchantId,
        TenantId = merchantId,
        Name = "ডেমো স্টোর",
        Msisdn = "8801711111111",
        Status = MerchantStatus.Trial,
    };

    db.Subscriptions[merchantId] = new Subscription
    {
        TenantId = merchantId,
        Calls = true,
        Payments = true,
        Courier = false,
        Plan = "trial",
    };

    db.Credentials["mp_dev_demo"] = new ApiCredential
    {
        TenantId = merchantId,
        KeyId = "mp_dev_demo",
        SecretCipher = "dev-secret-not-for-anything-real",
        KeyRingId = PlaintextSecretProtector.KeyRing,
        Label = "seeded demo key",
    };
}

public sealed record CreateOrderRequest(
    string? Reference,
    string? Msisdn,
    string? CustomerName,
    decimal Amount,
    string? Summary,
    string? Address,
    Dictionary<string, string>? Fields,
    string? CallbackUrl);

public sealed record CancelRequest(string? Reason);
