using System.Text.Json;
using System.Text.Json.Serialization;
using MoynaPay.Application.AppAuth;
using MoynaPay.Application.AppBootstrap;
using MoynaPay.Application.AppHome;
using MoynaPay.Application.AppOrders;
using MoynaPay.Api;
using MoynaPay.Application.Merchants;
using MoynaPay.Application.Abstractions;
using MoynaPay.Application.Orders;
using MoynaPay.Application.Workflows;
using MoynaPay.Domain.Merchants;
using MoynaPay.Domain.Orders;
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
builder.Services.AddMoynaPay(builder.Configuration);

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
builder.Services.AddScoped<AppHomeService>();
builder.Services.AddScoped<AppOrderService>();

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

// ---------------------------------------------------------------------------
// Merchant onboarding and API keys.
// ---------------------------------------------------------------------------
app.MapPost("/v1/merchants", async (
    CreateMerchantRequest request, MerchantService service, CancellationToken ct) =>
{
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
    Guid merchantId, IssueApiKeyRequest request, MerchantService service, CancellationToken ct) =>
{
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

static Task<AppPrincipal?> AppPrincipalAsync(HttpContext context, AppAuthService auth,
    CancellationToken ct)
{
    var header = context.Request.Headers.Authorization.ToString();
    const string bearer = "Bearer ";
    var token = header.StartsWith(bearer, StringComparison.OrdinalIgnoreCase)
        ? header[bearer.Length..].Trim()
        : null;

    return auth.ValidateAccessAsync(token, ct);
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
