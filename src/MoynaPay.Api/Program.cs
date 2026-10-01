using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using MoynaPay.Application.AppAuth;
using MoynaPay.Application.AppBootstrap;
using MoynaPay.Application.AppDevices;
using MoynaPay.Application.AppHome;
using MoynaPay.Application.AppOrders;
using MoynaPay.Application.AppSettings;
using MoynaPay.Api;
using MoynaPay.Application.Merchants;
using MoynaPay.Application.Abstractions;
using MoynaPay.Application.Orders;
using MoynaPay.Application.Payments.Ingestion;
using MoynaPay.Application.Payments.Matching;
using MoynaPay.Application.Workflows;
using MoynaPay.Domain.Merchants;
using MoynaPay.Domain.Orders;
using MoynaPay.Domain.Payments;
using MoynaPay.Infrastructure;
using MoynaPay.Infrastructure.Memory;
using MoynaPay.Infrastructure.Security;

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
builder.Services.AddMoynaPay(builder.Configuration, builder.Environment.IsDevelopment());

builder.Services.AddScoped<MerchantService>();
builder.Services.AddScoped<WebhookService>();
builder.Services.AddScoped<CreateOrderService>();
builder.Services.AddScoped<OrderTransitionService>();
builder.Services.AddScoped<OrderWorkflowService>();
builder.Services.AddScoped<OrderWorkflowRunner>();
builder.Services.AddScoped<WorkflowActionHandlers>();
builder.Services.AddScoped<AiProposalGate>();
builder.Services.AddScoped<ReviewQueueService>();
builder.Services.AddScoped<AppAuthService>();
builder.Services.AddScoped<AppBootstrapService>();
builder.Services.AddScoped<AppDevicesService>();
builder.Services.AddScoped<AppHomeService>();
builder.Services.AddScoped<AppOrderService>();
builder.Services.AddScoped<AppOrderActionService>();
builder.Services.AddScoped<AppSettingsService>();
builder.Services.AddScoped<RawEventIngestService>();

var app = builder.Build();

// Said out loud, every start. A service holding its merchants in memory looks healthy
// right up until it restarts and every order is gone.
if (DependencyInjection.IsInMemory(app.Configuration))
{
    app.Logger.LogWarning(
        "In-memory stores. Orders do not survive a restart, and the default key ring is " +
        "development only.");

    if (app.Environment.IsDevelopment())
    {
        var db = app.Services.GetRequiredService<MemoryDatabase>();
        Seed(db);
        var migrated = SecretCipherMigration.MigratePlaintext(
            db, app.Services.GetRequiredService<ISecretProtector>());
        if (migrated > 0)
        {
            app.Logger.LogInformation("Migrated {Count} plaintext development secrets.", migrated);
        }
    }
}

app.UseMiddleware<SignedRequestMiddleware>();

app.MapGet("/v1/health", () => Results.Ok(new { status = "ok", service = "MoynaPay" }));

// ---------------------------------------------------------------------------
// Merchant app auth.
// ---------------------------------------------------------------------------
app.MapPost("/app/v1/auth/otp", async (
    AppOtpRequest request, AppAuthService auth, CancellationToken ct) =>
{
    var result = await auth.RequestOtpAsync(request.Phone, ct).ConfigureAwait(false);

    return result.Outcome switch
    {
        RequestOtpOutcome.Sent => Results.Ok(new
        {
            sent = true,
            expiresInSeconds = (int)AppAuthService.OtpTtl.TotalSeconds,
            devOtp = app.Environment.IsDevelopment() ? result.DevOtp : null,
        }),
        RequestOtpOutcome.RateLimited => Results.Problem(
            title: result.Reason, statusCode: StatusCodes.Status429TooManyRequests),
        RequestOtpOutcome.NoMerchant => Results.NotFound(),
        _ => Results.Problem(title: result.Reason, statusCode: StatusCodes.Status400BadRequest),
    };
});

app.MapPost("/app/v1/auth/token", async (
    AppOtpVerifyRequest request, AppAuthService auth, CancellationToken ct) =>
{
    var result = await auth.VerifyOtpAsync(request.Phone, request.Otp, ct).ConfigureAwait(false);

    return AppTokenResponse(result);
});

app.MapPost("/app/v1/auth/refresh", async (
    AppRefreshRequest request, AppAuthService auth, CancellationToken ct) =>
{
    var result = await auth.RefreshAsync(request.RefreshToken, ct).ConfigureAwait(false);

    return AppTokenResponse(result);
});

app.MapPost("/app/v1/auth/logout", async (
    AppRefreshRequest request, AppAuthService auth, CancellationToken ct) =>
{
    await auth.LogoutAsync(request.RefreshToken, ct).ConfigureAwait(false);

    return Results.NoContent();
});

app.MapGet("/app/v1/bootstrap", async (
    HttpContext context, AppAuthService auth, AppBootstrapService bootstrap,
    CancellationToken ct) =>
{
    var principal = await AppPrincipalAsync(context, auth, ct).ConfigureAwait(false);
    if (principal is null) return Results.Unauthorized();

    var result = await bootstrap.GetAsync(principal.MerchantId, ct).ConfigureAwait(false);

    return result.Found ? Results.Ok(result.Bootstrap) : Results.Unauthorized();
});

app.MapGet("/app/v1/home/numbers", async (
    HttpContext context, AppAuthService auth, AppHomeService home, CancellationToken ct) =>
{
    var principal = await AppPrincipalAsync(context, auth, ct).ConfigureAwait(false);
    if (principal is null) return Results.Unauthorized();

    return Results.Ok(await home.NumbersAsync(principal.MerchantId, ct).ConfigureAwait(false));
});

app.MapGet("/app/v1/orders", async (
    HttpContext context, string? status, string? search, int? take,
    DateTimeOffset? before, DateTimeOffset? from, DateTimeOffset? to,
    AppAuthService auth, AppOrderService appOrders, CancellationToken ct) =>
{
    var principal = await AppPrincipalAsync(context, auth, ct).ConfigureAwait(false);
    if (principal is null) return Results.Unauthorized();

    if (!TryParseStatus(status, out var parsedStatus))
    {
        return Results.Problem(title: "Invalid order status.", statusCode: StatusCodes.Status400BadRequest);
    }

    return Results.Ok(await appOrders.ListAsync(principal.MerchantId, new AppOrderListQuery
    {
        Status = parsedStatus,
        Search = search,
        Take = take ?? 50,
        Before = before,
        From = from,
        To = to,
    }, ct).ConfigureAwait(false));
});

app.MapGet("/app/v1/orders/{reference}", async (
    string reference, HttpContext context, AppAuthService auth, AppOrderService appOrders,
    CancellationToken ct) =>
{
    var principal = await AppPrincipalAsync(context, auth, ct).ConfigureAwait(false);
    if (principal is null) return Results.Unauthorized();

    var order = await appOrders.DetailAsync(principal.MerchantId, reference, ct).ConfigureAwait(false);

    return order is null ? Results.NotFound() : Results.Ok(order);
});

app.MapGet("/app/v1/orders/{reference}/timeline", async (
    string reference, HttpContext context, AppAuthService auth, AppOrderService appOrders,
    CancellationToken ct) =>
{
    var principal = await AppPrincipalAsync(context, auth, ct).ConfigureAwait(false);
    if (principal is null) return Results.Unauthorized();

    var order = await appOrders.DetailAsync(principal.MerchantId, reference, ct).ConfigureAwait(false);
    if (order is null) return Results.NotFound();

    return Results.Ok(await appOrders.TimelineAsync(principal.MerchantId, reference, ct).ConfigureAwait(false));
});

app.MapPost("/app/v1/orders/{reference}/decision", async (
    string reference, AppOrderDecisionRequest request, HttpContext context,
    AppAuthService auth, AppOrderActionService actions, CancellationToken ct) =>
{
    var principal = await AppPrincipalAsync(context, auth, ct).ConfigureAwait(false);
    if (principal is null) return Results.Unauthorized();

    var actor = request.ActorName ?? principal.MerchantName;
    var result = await actions.DecideAsync(
        principal.MerchantId, reference, request.Decision, actor, request.Reason, ct)
        .ConfigureAwait(false);

    return AppOrderActionResponse(result);
});

app.MapPost("/app/v1/orders/{reference}/recall", async (
    string reference, AppOrderReasonRequest request, HttpContext context,
    AppAuthService auth, AppOrderActionService actions, CancellationToken ct) =>
{
    var principal = await AppPrincipalAsync(context, auth, ct).ConfigureAwait(false);
    if (principal is null) return Results.Unauthorized();

    var result = await actions.RecallAsync(
        principal.MerchantId, reference, request.ActorName ?? principal.MerchantName,
        request.Reason, ct).ConfigureAwait(false);

    return AppOrderActionResponse(result);
});

app.MapPost("/app/v1/orders/{reference}/claim", async (
    string reference, AppReviewClaimActionRequest request, HttpContext context,
    AppAuthService auth, AppOrderActionService actions, CancellationToken ct) =>
{
    var principal = await AppPrincipalAsync(context, auth, ct).ConfigureAwait(false);
    if (principal is null) return Results.Unauthorized();

    var result = await actions.ClaimReviewAsync(
        principal.MerchantId, reference, request.Reviewer ?? principal.MerchantName,
        TimeSpan.FromSeconds(request.ClaimSeconds ?? 300), ct).ConfigureAwait(false);

    return result.Outcome switch
    {
        ReviewClaimOutcome.Claimed => Results.Ok(result.Order),
        ReviewClaimOutcome.NotFound => Results.NotFound(),
        ReviewClaimOutcome.Invalid => Results.Problem(
            title: result.Reason, statusCode: StatusCodes.Status400BadRequest),
        _ => Results.Problem(title: result.Reason, statusCode: StatusCodes.Status409Conflict),
    };
});

app.MapPost("/app/v1/orders/{reference}/ship", async (
    string reference, AppOrderReasonRequest request, HttpContext context,
    AppAuthService auth, AppOrderActionService actions, CancellationToken ct) =>
{
    var principal = await AppPrincipalAsync(context, auth, ct).ConfigureAwait(false);
    if (principal is null) return Results.Unauthorized();

    var result = await actions.MarkShippedAsync(
        principal.MerchantId, reference, request.ActorName ?? principal.MerchantName,
        request.Reason, ct).ConfigureAwait(false);

    return AppOrderActionResponse(result);
});

app.MapGet("/app/v1/settings", async (
    HttpContext context, AppAuthService auth, AppSettingsService settings,
    CancellationToken ct) =>
{
    var principal = await AppPrincipalAsync(context, auth, ct).ConfigureAwait(false);
    if (principal is null) return Results.Unauthorized();

    var result = await settings.GetAsync(principal.MerchantId, ct).ConfigureAwait(false);

    return result.Found ? Results.Ok(result.Settings) : Results.Unauthorized();
});

app.MapPut("/app/v1/settings/profile", async (
    AppProfileSettingsRequest request, HttpContext context, AppAuthService auth,
    AppSettingsService settings, CancellationToken ct) =>
{
    var principal = await AppPrincipalAsync(context, auth, ct).ConfigureAwait(false);
    if (principal is null) return Results.Unauthorized();

    var result = await settings.UpdateProfileAsync(principal.MerchantId, new UpdateProfileSettings(
        request.Name, request.TimeZone, request.Address, request.SupportMsisdn), ct)
        .ConfigureAwait(false);

    return result.Found ? Results.Ok(result.Settings) : Results.Unauthorized();
});

app.MapPut("/app/v1/settings/webhook", async (
    AppWebhookSettingsRequest request, HttpContext context, AppAuthService auth,
    AppSettingsService settings, CancellationToken ct) =>
{
    var principal = await AppPrincipalAsync(context, auth, ct).ConfigureAwait(false);
    if (principal is null) return Results.Unauthorized();

    var result = await settings.UpdateWebhookAsync(
        principal.MerchantId, request.Url, request.Active ?? true, ct).ConfigureAwait(false);

    return result.Outcome switch
    {
        WebhookOutcome.Created => Results.Created("/app/v1/settings/webhook", WebhookView(result.Endpoint!)),
        WebhookOutcome.Updated => Results.Ok(WebhookView(result.Endpoint!)),
        WebhookOutcome.Invalid => Results.Problem(title: result.Reason, statusCode: StatusCodes.Status400BadRequest),
        _ => Results.NotFound(),
    };
});

app.MapGet("/app/v1/devices", async (
    HttpContext context, AppAuthService auth, AppDevicesService devices,
    CancellationToken ct) =>
{
    var principal = await AppPrincipalAsync(context, auth, ct).ConfigureAwait(false);
    if (principal is null) return Results.Unauthorized();

    return Results.Ok(await devices.ListAsync(principal.MerchantId, ct).ConfigureAwait(false));
});

app.MapPost("/app/v1/devices/pairing-token", async (
    HttpContext context, AppAuthService auth, AppDevicesService devices,
    CancellationToken ct) =>
{
    var principal = await AppPrincipalAsync(context, auth, ct).ConfigureAwait(false);
    if (principal is null) return Results.Unauthorized();

    var result = await devices.CreatePairingTokenAsync(principal.MerchantId, ct)
        .ConfigureAwait(false);

    return Results.Created("/app/v1/devices/pairing-token", new
    {
        pairingToken = result.Token,
        expiresAt = result.ExpiresAt,
        expiresInSeconds = (int)AppDevicesService.PairingTtl.TotalSeconds,
    });
});

app.MapPost("/app/v1/devices/pair", async (
    AppDevicePairRequest request, AppDevicesService devices, CancellationToken ct) =>
{
    var result = await devices.PairAsync(new PairDeviceCommand(
        request.PairingToken,
        request.Fingerprint,
        request.Name,
        request.Model,
        request.AppVersion,
        request.PushToken,
        request.PermissionState,
        request.BatteryPercent,
        request.NetworkType), ct).ConfigureAwait(false);

    return result.Outcome switch
    {
        PairDeviceOutcome.Paired => Results.Created($"/app/v1/devices/{result.Device!.Id}", new
        {
            device = result.Device,
            deviceToken = result.DeviceToken,
        }),
        _ => Results.Problem(title: result.Reason, statusCode: StatusCodes.Status400BadRequest),
    };
});

app.MapPost("/app/v1/devices/{deviceId:guid}/heartbeat", async (
    Guid deviceId, AppDeviceHeartbeatRequest request, HttpContext context,
    AppDevicesService devices, CancellationToken ct) =>
{
    var result = await devices.HeartbeatAsync(
        deviceId, DeviceToken(context), new DeviceHeartbeatCommand(
            request.PermissionState,
            request.BatteryPercent,
            request.NetworkType,
            request.AppVersion,
            request.Model), ct).ConfigureAwait(false);

    return DeviceUpdateResponse(result);
});

app.MapPut("/app/v1/devices/{deviceId:guid}/push-token", async (
    Guid deviceId, AppDevicePushTokenRequest request, HttpContext context,
    AppDevicesService devices, CancellationToken ct) =>
{
    var result = await devices.UpdatePushTokenAsync(
        deviceId, DeviceToken(context), request.PushToken, ct).ConfigureAwait(false);

    return DeviceUpdateResponse(result);
});

app.MapPost("/app/v1/devices/{deviceId:guid}/events", async (
    Guid deviceId, AppDeviceEventsRequest request, HttpContext context,
    AppDevicesService devices, RawEventIngestService ingest, CancellationToken ct) =>
{
    var device = await devices.AuthenticateAsync(deviceId, DeviceToken(context), ct)
        .ConfigureAwait(false);
    if (device is null) return Results.Unauthorized();

    var events = request.Events ?? [];
    var result = await ingest.IngestAsync(
        device,
        events.Select(e => new RawDeviceEvent(
                e.Source,
                e.SenderId,
                e.Body,
                e.ReceivedAt ?? DateTimeOffset.MinValue))
            .ToList(),
        ct).ConfigureAwait(false);

    return Results.Ok(result);
});

// ---------------------------------------------------------------------------
// Merchant onboarding and API keys.
// ---------------------------------------------------------------------------
// Neither of these two routes is signed - a merchant who does not exist yet has no key to
// sign with - so both are gated on an operator credential instead. A merchant id is not a
// credential: it appears in URLs, in logs, and in the body of a webhook.test posted to a
// third party, and before this check anyone holding one could ask for that shop's signing
// secret and then sign as the shop.
app.MapPost("/v1/merchants", async (
    CreateMerchantRequest request, HttpContext context, IConfiguration config,
    MerchantService service, CancellationToken ct) =>
{
    if (RefuseOperator(context, config) is { } refusal) return refusal;

    var result = await service.CreateAsync(new CreateMerchantCommand
    {
        Name = request.Name,
        Msisdn = request.Msisdn,
        TimeZone = request.TimeZone,
        Address = request.Address,
        SupportMsisdn = request.SupportMsisdn,
        Calls = request.Calls,
        Payments = request.Payments,
        Courier = request.Courier,
        Plan = request.Plan,
    }, ct).ConfigureAwait(false);

    if (result.Outcome == CreateMerchantOutcome.Invalid)
    {
        return Results.Problem(title: result.Reason, statusCode: StatusCodes.Status400BadRequest);
    }

    return Results.Created($"/v1/merchants/{result.Merchant!.Id}", MerchantView(result.Merchant));
});

app.MapPost("/v1/merchants/{merchantId:guid}/api-keys", async (
    Guid merchantId, IssueApiKeyRequest request, HttpContext context, IConfiguration config,
    MerchantService service, CancellationToken ct) =>
{
    if (RefuseOperator(context, config) is { } refusal) return refusal;

    var result = await service.IssueKeyAsync(merchantId, request.Label, bootstrapOnly: true, ct)
        .ConfigureAwait(false);

    return result.Outcome switch
    {
        IssueApiKeyOutcome.Issued => Results.Created(
            $"/v1/api-keys/{result.Credential!.KeyId}", ApiKeyIssuedView(result.Credential, result.Secret!)),
        IssueApiKeyOutcome.NoMerchant => Results.NotFound(),
        _ => Results.Problem(title: result.Reason, statusCode: StatusCodes.Status409Conflict),
    };
});

app.MapGet("/v1/api-keys", async (
    HttpContext context, MerchantService service, CancellationToken ct) =>
{
    if (context.Items[SignedRequestMiddleware.MerchantItem] is not Guid merchantId)
    {
        return Results.Unauthorized();
    }

    var keys = await service.ListKeysAsync(merchantId, ct).ConfigureAwait(false);

    return Results.Ok(keys.Select(ApiKeyView));
});

app.MapPost("/v1/api-keys", async (
    IssueApiKeyRequest request, HttpContext context, MerchantService service, CancellationToken ct) =>
{
    if (context.Items[SignedRequestMiddleware.MerchantItem] is not Guid merchantId)
    {
        return Results.Unauthorized();
    }

    var result = await service.IssueKeyAsync(merchantId, request.Label, ct: ct).ConfigureAwait(false);

    return result.Outcome switch
    {
        IssueApiKeyOutcome.Issued => Results.Created(
            $"/v1/api-keys/{result.Credential!.KeyId}", ApiKeyIssuedView(result.Credential, result.Secret!)),
        IssueApiKeyOutcome.NoMerchant => Results.NotFound(),
        _ => Results.Problem(title: result.Reason, statusCode: StatusCodes.Status409Conflict),
    };
});

app.MapDelete("/v1/api-keys/{keyId}", async (
    string keyId, HttpContext context, MerchantService service, CancellationToken ct) =>
{
    if (context.Items[SignedRequestMiddleware.MerchantItem] is not Guid merchantId)
    {
        return Results.Unauthorized();
    }

    return await service.RevokeKeyAsync(merchantId, keyId, ct).ConfigureAwait(false)
        ? Results.NoContent()
        : Results.NotFound();
});

app.MapGet("/v1/webhook/endpoint", async (
    HttpContext context, IMerchantStore merchants, CancellationToken ct) =>
{
    if (context.Items[SignedRequestMiddleware.MerchantItem] is not Guid merchantId)
    {
        return Results.Unauthorized();
    }

    var endpoint = await merchants.WebhookAsync(merchantId, ct).ConfigureAwait(false);

    return endpoint is null ? Results.NotFound() : Results.Ok(WebhookView(endpoint));
});

app.MapPut("/v1/webhook/endpoint", async (
    RegisterWebhookRequest request, HttpContext context, WebhookService service,
    CancellationToken ct) =>
{
    if (context.Items[SignedRequestMiddleware.MerchantItem] is not Guid merchantId)
    {
        return Results.Unauthorized();
    }

    var result = await service.RegisterAsync(merchantId, request.Url, request.Active ?? true, ct)
        .ConfigureAwait(false);

    return result.Outcome switch
    {
        WebhookOutcome.Created => Results.Created("/v1/webhook/endpoint", WebhookView(result.Endpoint!)),
        WebhookOutcome.Updated => Results.Ok(WebhookView(result.Endpoint!)),
        WebhookOutcome.Invalid => Results.Problem(title: result.Reason, statusCode: StatusCodes.Status400BadRequest),
        WebhookOutcome.NoMerchant => Results.NotFound(),
        _ => Results.Problem(statusCode: StatusCodes.Status409Conflict),
    };
});

app.MapPost("/v1/webhook/endpoint/test", async (
    HttpContext context, WebhookService service, CancellationToken ct) =>
{
    if (context.Items[SignedRequestMiddleware.MerchantItem] is not Guid merchantId)
    {
        return Results.Unauthorized();
    }

    var result = await service.SendTestAsync(merchantId, ct).ConfigureAwait(false);

    return result.Outcome switch
    {
        WebhookTestOutcome.Delivered => Results.Ok(WebhookTestView(result)),
        WebhookTestOutcome.Failed => Results.Problem(
            title: result.FailureReason, statusCode: StatusCodes.Status502BadGateway),
        _ => Results.NotFound(),
    };
});

app.MapPost("/v1/webhook/endpoint/rotate-secret", async (
    HttpContext context, WebhookService service, CancellationToken ct) =>
{
    if (context.Items[SignedRequestMiddleware.MerchantItem] is not Guid merchantId)
    {
        return Results.Unauthorized();
    }

    var result = await service.RotateSecretAsync(merchantId, ct).ConfigureAwait(false);

    return result.Outcome == WebhookOutcome.Updated
        ? Results.Ok(WebhookView(result.Endpoint!))
        : Results.NotFound();
});

// ---------------------------------------------------------------------------
// Orders, from the shop.
// ---------------------------------------------------------------------------
app.MapPost("/v1/orders", async (
    CreateOrderRequest request, HttpContext context, OrderWorkflowService workflow,
    CancellationToken ct) =>
{
    if (context.Items[SignedRequestMiddleware.MerchantItem] is not Guid merchantId)
    {
        return Results.Unauthorized();
    }

    var result = await workflow.CreateAsync(new CreateOrderCommand
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
    IOrderStore orders, OrderWorkflowService workflow, CancellationToken ct) =>
{
    if (context.Items[SignedRequestMiddleware.MerchantItem] is not Guid merchantId)
    {
        return Results.Unauthorized();
    }

    var order = await orders.FindByReferenceAsync(merchantId, reference, ct).ConfigureAwait(false);
    if (order is null) return Results.NotFound();

    var result = await workflow.DecideAsync(new WorkflowDecisionCommand
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

// ---------------------------------------------------------------------------
// Review queue, for orders the machine cannot safely decide.
// ---------------------------------------------------------------------------
app.MapGet("/v1/review/orders", async (
    HttpContext context, ReviewQueueService reviews, CancellationToken ct) =>
{
    if (context.Items[SignedRequestMiddleware.MerchantItem] is not Guid merchantId)
    {
        return Results.Unauthorized();
    }

    var rows = await reviews.ListAvailableAsync(merchantId, ct: ct).ConfigureAwait(false);

    return Results.Ok(rows.Select(ReviewView));
});

app.MapPost("/v1/review/orders/{reference}/claim", async (
    string reference, ReviewClaimRequest request, HttpContext context,
    IOrderStore orders, ReviewQueueService reviews, CancellationToken ct) =>
{
    if (context.Items[SignedRequestMiddleware.MerchantItem] is not Guid merchantId)
    {
        return Results.Unauthorized();
    }

    var order = await orders.FindByReferenceAsync(merchantId, reference, ct).ConfigureAwait(false);
    if (order is null) return Results.NotFound();

    var result = await reviews.ClaimAsync(new ReviewClaimCommand
    {
        MerchantId = merchantId,
        OrderId = order.Id,
        Reviewer = request.Reviewer,
        ClaimFor = TimeSpan.FromSeconds(request.ClaimSeconds ?? 300),
    }, ct).ConfigureAwait(false);

    return result.Outcome switch
    {
        ReviewClaimOutcome.Claimed => Results.Ok(ReviewView(result.Order!)),
        ReviewClaimOutcome.NotFound => Results.NotFound(),
        ReviewClaimOutcome.Invalid => Results.Problem(
            title: result.Reason, statusCode: StatusCodes.Status400BadRequest),
        _ => Results.Problem(title: result.Reason, statusCode: StatusCodes.Status409Conflict),
    };
});

app.MapPost("/v1/review/orders/{reference}/release", async (
    string reference, ReviewReleaseRequest request, HttpContext context,
    IOrderStore orders, ReviewQueueService reviews, CancellationToken ct) =>
{
    if (context.Items[SignedRequestMiddleware.MerchantItem] is not Guid merchantId)
    {
        return Results.Unauthorized();
    }

    var order = await orders.FindByReferenceAsync(merchantId, reference, ct).ConfigureAwait(false);
    if (order is null) return Results.NotFound();

    var result = await reviews.ReleaseAsync(new ReviewReleaseCommand
    {
        MerchantId = merchantId,
        OrderId = order.Id,
        Reviewer = request.Reviewer,
    }, ct).ConfigureAwait(false);

    return result.Outcome switch
    {
        ReviewReleaseOutcome.Released => Results.Ok(ReviewView(result.Order!)),
        ReviewReleaseOutcome.NotFound => Results.NotFound(),
        ReviewReleaseOutcome.Invalid => Results.Problem(
            title: result.Reason, statusCode: StatusCodes.Status400BadRequest),
        _ => Results.Problem(title: result.Reason, statusCode: StatusCodes.Status409Conflict),
    };
});

app.MapPost("/v1/review/orders/{reference}/outcome", async (
    string reference, ReviewOutcomeRequest request, HttpContext context,
    IOrderStore orders, ReviewQueueService reviews, CancellationToken ct) =>
{
    if (context.Items[SignedRequestMiddleware.MerchantItem] is not Guid merchantId)
    {
        return Results.Unauthorized();
    }

    var order = await orders.FindByReferenceAsync(merchantId, reference, ct).ConfigureAwait(false);
    if (order is null) return Results.NotFound();

    var result = await reviews.DecideAsync(new ReviewDecisionCommand
    {
        MerchantId = merchantId,
        OrderId = order.Id,
        Reviewer = request.Reviewer,
        Outcome = request.Outcome,
        Reason = request.Reason,
    }, ct).ConfigureAwait(false);

    return result.Outcome switch
    {
        ReviewDecisionOutcome.Moved or ReviewDecisionOutcome.Unchanged => Results.Ok(View(result.Order!)),
        ReviewDecisionOutcome.NotFound => Results.NotFound(),
        ReviewDecisionOutcome.Invalid => Results.Problem(
            title: result.Reason, statusCode: StatusCodes.Status400BadRequest),
        _ => Results.Problem(title: result.Reason, statusCode: StatusCodes.Status409Conflict),
    };
});

app.MapGet("/v1/review/payments", async (
    HttpContext context, PaymentReviewService reviews, CancellationToken ct) =>
{
    if (context.Items[SignedRequestMiddleware.MerchantItem] is not Guid merchantId)
    {
        return Results.Unauthorized();
    }

    return Results.Ok(await reviews.ListAsync(merchantId, ct).ConfigureAwait(false));
});

app.MapPost("/v1/review/payments/{rawEventId:guid}/match", async (
    Guid rawEventId, ManualPaymentMatchRequest request, HttpContext context,
    PaymentReviewService reviews, CancellationToken ct) =>
{
    if (context.Items[SignedRequestMiddleware.MerchantItem] is not Guid merchantId)
    {
        return Results.Unauthorized();
    }

    var result = await reviews.ManualMatchAsync(new ManualPaymentMatchCommand(
        merchantId,
        rawEventId,
        request.OrderRef ?? "",
        request.Reviewer,
        request.Reason), ct).ConfigureAwait(false);

    return result.Outcome switch
    {
        ManualPaymentMatchOutcome.Matched => Results.Ok(result.Invoice),
        ManualPaymentMatchOutcome.NotFound or ManualPaymentMatchOutcome.InvoiceNotFound => Results.NotFound(),
        ManualPaymentMatchOutcome.Invalid => Results.Problem(
            title: result.Reason, statusCode: StatusCodes.Status400BadRequest),
        _ => Results.Problem(title: "Payment is already matched.", statusCode: StatusCodes.Status409Conflict),
    };
});

app.Run();

/// <summary>
/// The gate on the two unsigned onboarding routes. Null means let it through.
///
/// With no token configured the routes are open on a laptop, where there is nothing yet to
/// protect, and closed everywhere else. Closed rather than open, because the failure of the
/// open version is silent: the service starts, answers, and hands out signing secrets.
/// </summary>
static IResult? RefuseOperator(HttpContext context, IConfiguration config)
{
    var expected = config["MoynaPay:OperatorToken"];

    if (string.IsNullOrWhiteSpace(expected))
    {
        var environment = context.RequestServices.GetRequiredService<IWebHostEnvironment>();

        return environment.IsDevelopment()
            ? null
            : Results.Problem(
                title: "Onboarding is closed: set MoynaPay:OperatorToken to open it.",
                statusCode: StatusCodes.Status503ServiceUnavailable);
    }

    var presented = context.Request.Headers["X-MoynaPay-Operator"].ToString();

    return SameSecret(expected, presented) ? null : Results.Unauthorized();
}

/// <summary>
/// Compared as digests rather than as bytes, so neither the timing nor the length of the
/// comparison says anything about the token. Guessing it one character at a time is the
/// attack an ordinary string equality allows.
/// </summary>
static bool SameSecret(string expected, string presented) =>
    CryptographicOperations.FixedTimeEquals(
        SHA256.HashData(Encoding.UTF8.GetBytes(expected)),
        SHA256.HashData(Encoding.UTF8.GetBytes(presented)));

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
    claimedBy = order.ClaimedBy,
    claimedUntil = order.ClaimedUntil,
};

static object ReviewView(Order order) => new
{
    reference = order.Reference,
    status = order.Status,
    customerName = order.CustomerName,
    msisdn = order.Msisdn,
    amount = order.Amount,
    summary = order.Summary,
    reason = order.Reason,
    claimedBy = order.ClaimedBy,
    claimedUntil = order.ClaimedUntil,
    createdAt = order.CreatedAt,
};

static object MerchantView(Merchant merchant) => new
{
    id = merchant.Id,
    name = merchant.Name,
    msisdn = merchant.Msisdn,
    status = merchant.Status,
    timeZone = merchant.TimeZone,
    address = merchant.Address,
    supportMsisdn = merchant.SupportMsisdn,
    createdAt = merchant.CreatedAt,
};

static object ApiKeyView(ApiCredential credential) => new
{
    keyId = credential.KeyId,
    label = credential.Label,
    createdAt = credential.CreatedAt,
    lastUsedAt = credential.LastUsedAt,
    revokedAt = credential.RevokedAt,
    active = credential.IsActive,
};

static object ApiKeyIssuedView(ApiCredential credential, string secret) => new
{
    keyId = credential.KeyId,
    secret,
    label = credential.Label,
    createdAt = credential.CreatedAt,
};

static object WebhookView(WebhookEndpoint endpoint) => new
{
    url = endpoint.Url,
    active = endpoint.Active,
    lastDeliveredAt = endpoint.LastDeliveredAt,
    lastFailureReason = endpoint.LastFailureReason,
    createdAt = endpoint.CreatedAt,
    updatedAt = endpoint.UpdatedAt,
};

static object WebhookTestView(WebhookTestResult result) => new
{
    delivered = result.Outcome == WebhookTestOutcome.Delivered,
    signature = result.Signature,
    body = result.Body,
};

static IResult AppTokenResponse(AppTokenResult result) =>
    result.Outcome switch
    {
        AppTokenOutcome.Issued => Results.Ok(new
        {
            accessToken = result.AccessToken,
            refreshToken = result.RefreshToken,
            tokenType = "Bearer",
            expiresInSeconds = (int)AppAuthService.AccessTtl.TotalSeconds,
        }),
        AppTokenOutcome.RateLimited => Results.Problem(
            title: result.Reason, statusCode: StatusCodes.Status429TooManyRequests),
        _ => Results.Problem(title: result.Reason, statusCode: StatusCodes.Status401Unauthorized),
    };

static async Task<AppPrincipal?> AppPrincipalAsync(HttpContext context, AppAuthService auth,
    CancellationToken ct)
{
    var header = context.Request.Headers.Authorization.ToString();
    const string bearer = "Bearer ";
    var token = header.StartsWith(bearer, StringComparison.OrdinalIgnoreCase)
        ? header[bearer.Length..].Trim()
        : null;

    var principal = await auth.ValidateAccessAsync(token, ct).ConfigureAwait(false);

    // The app has no signing middleware in front of it - the token is validated here, in
    // the one place every /app/v1 route goes through - so this is where the database's
    // query filter learns whose request it is. Without it the filter defends nothing on
    // the app side, and every store method's merchantId parameter is the only thing
    // standing between one merchant and another's orders.
    if (principal is not null)
    {
        context.RequestServices.GetRequiredService<ITenantContext>().Set(principal.MerchantId);
    }

    return principal;
}

static bool TryParseStatus(string? status, out OrderStatus? parsed)
{
    parsed = null;
    if (string.IsNullOrWhiteSpace(status)) return true;

    if (!Enum.TryParse<OrderStatus>(status.Trim(), ignoreCase: true, out var value))
    {
        return false;
    }

    parsed = value;
    return true;
}

static IResult AppOrderActionResponse(AppOrderActionResult result) =>
    result.Outcome switch
    {
        AppOrderActionOutcome.Moved or AppOrderActionOutcome.Unchanged => Results.Ok(result.Order),
        AppOrderActionOutcome.NotFound => Results.NotFound(),
        _ => Results.Problem(title: result.Reason, statusCode: StatusCodes.Status409Conflict),
    };

static string? DeviceToken(HttpContext context)
{
    var header = context.Request.Headers["X-MoynaPay-Device-Token"].ToString();
    return string.IsNullOrWhiteSpace(header) ? null : header;
}

static IResult DeviceUpdateResponse(DeviceUpdateResult result) =>
    result.Outcome switch
    {
        DeviceUpdateOutcome.Updated => Results.Ok(result.Device),
        DeviceUpdateOutcome.Unauthorized => Results.Unauthorized(),
        _ => Results.Problem(title: result.Reason, statusCode: StatusCodes.Status400BadRequest),
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

public sealed record ReviewClaimRequest(string? Reviewer, int? ClaimSeconds);

public sealed record ReviewReleaseRequest(string? Reviewer);

public sealed record ReviewOutcomeRequest(string? Reviewer, ReviewDecision Outcome, string? Reason);

public sealed record CreateMerchantRequest(
    string? Name,
    string? Msisdn,
    string? TimeZone,
    string? Address,
    string? SupportMsisdn,
    bool Calls,
    bool Payments,
    bool Courier,
    string? Plan);

public sealed record IssueApiKeyRequest(string? Label);

public sealed record RegisterWebhookRequest(string? Url, bool? Active);

public sealed record AppOtpRequest(string? Phone);

public sealed record AppOtpVerifyRequest(string? Phone, string? Otp);

public sealed record AppRefreshRequest(string? RefreshToken);

public sealed record AppOrderDecisionRequest(AppDecision Decision, string? ActorName, string? Reason);

public sealed record AppOrderReasonRequest(string? ActorName, string? Reason);

public sealed record AppReviewClaimActionRequest(string? Reviewer, int? ClaimSeconds);

public sealed record AppProfileSettingsRequest(
    string? Name,
    string? TimeZone,
    string? Address,
    string? SupportMsisdn);

public sealed record AppWebhookSettingsRequest(string? Url, bool? Active);

public sealed record AppDevicePairRequest(
    string? PairingToken,
    string? Fingerprint,
    string? Name,
    string? Model,
    string? AppVersion,
    string? PushToken,
    DevicePermissionState? PermissionState,
    int? BatteryPercent,
    string? NetworkType);

public sealed record AppDeviceHeartbeatRequest(
    DevicePermissionState? PermissionState,
    int? BatteryPercent,
    string? NetworkType,
    string? AppVersion,
    string? Model);

public sealed record AppDevicePushTokenRequest(string? PushToken);

public sealed record AppDeviceEventsRequest(IReadOnlyList<AppDeviceEventRequest>? Events);

public sealed record AppDeviceEventRequest(
    EventSource? Source,
    string? SenderId,
    string? Body,
    DateTimeOffset? ReceivedAt);

public sealed record ManualPaymentMatchRequest(
    string? OrderRef,
    string? Reviewer,
    string? Reason);
