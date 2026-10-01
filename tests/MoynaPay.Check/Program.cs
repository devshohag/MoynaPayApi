using System.Security.Cryptography;
using MoynaPay.Application.AppAuth;
using MoynaPay.Application.AppBootstrap;
using MoynaPay.Application.AppDevices;
using MoynaPay.Application.AppHome;
using MoynaPay.Application.AppOrders;
using MoynaPay.Application.AppSettings;
using MoynaPay.Application.Abstractions;
using MoynaPay.Application.Merchants;
using MoynaPay.Application.Orders;
using MoynaPay.Application.Payments.Ingestion;
using MoynaPay.Application.Payments.Matching;
using MoynaPay.Application.Payments.Parsing;
using MoynaPay.Application.Security;
using MoynaPay.Application.Voice;
using MoynaPay.Application.Voice.Ai;
using MoynaPay.Application.Voice.Recording;
using MoynaPay.Application.Voice.Speech;
using MoynaPay.Application.Workflows;
using MoynaPay.Domain.Merchants;
using MoynaPay.Domain.Orders;
using MoynaPay.Domain.Payments;
using MoynaPay.Domain.Voice;
using MoynaPay.Infrastructure.Memory;
using MoynaPay.Infrastructure.Security;

// Every rule in phase 1, checked without a database, a network or a key.
//
// These are not tests of C#. They are the rules about somebody's money: who may confirm
// an order, who may reject one, what a retry does, and what a forged request cannot do.
//
//   dotnet run --project tools/MoynaPay.Check

int pass = 0, fail = 0;

void Check(string name, Func<bool> f)
{
    try
    {
        if (f()) pass++;
        else { fail++; Console.WriteLine($"FAIL  {name}"); }
    }
    catch (Exception e)
    {
        fail++;
        Console.WriteLine($"THREW {name}: {e.GetType().Name} {e.Message}");
    }
}

async Task CheckAsync(string name, Func<Task<bool>> f)
{
    try
    {
        if (await f()) pass++;
        else { fail++; Console.WriteLine($"FAIL  {name}"); }
    }
    catch (Exception e)
    {
        fail++;
        Console.WriteLine($"THREW {name}: {e.GetType().Name} {e.Message}");
    }
}

// ---------------------------------------------------------------------------
// Phone numbers
// ---------------------------------------------------------------------------
Check("plain local number", () => Msisdn.Normalise("01711223344") == "8801711223344");
Check("with country code", () => Msisdn.Normalise("+8801711223344") == "8801711223344");
Check("with 00 prefix", () => Msisdn.Normalise("008801711223344") == "8801711223344");
Check("spaces and dashes", () => Msisdn.Normalise(" 01711-22 33 44 ") == "8801711223344");
Check("leading zero lost in a spreadsheet", () => Msisdn.Normalise("1711223344") == "8801711223344");
Check("too short is refused", () => Msisdn.Normalise("0171122") is null);
Check("landline is refused", () => Msisdn.Normalise("029876543") is null);
Check("unknown prefix is refused", () => Msisdn.Normalise("01211223344") is null);
Check("null is refused", () => Msisdn.Normalise(null) is null);
Check("pretty form for display", () => Msisdn.Pretty("8801711223344") == "01711-223344");

// ---------------------------------------------------------------------------
// The lifecycle
// ---------------------------------------------------------------------------
Check("a call can confirm", () =>
    OrderLifecycle.CanMove(OrderStatus.Calling, OrderStatus.Confirmed, Actor.Machine));

Check("a keypress of zero can reject", () =>
    OrderLifecycle.CanMove(OrderStatus.Calling, OrderStatus.Rejected, Actor.Machine));

Check("the machine can send an order for review", () =>
    OrderLifecycle.CanMove(OrderStatus.Calling, OrderStatus.NeedsHuman, Actor.Machine));

// The rule the whole product rests on. An order the machine could not read is not a
// customer saying no, and no worker may turn it into one.
Check("the machine may NOT reject an order that went for review", () =>
    !OrderLifecycle.CanMove(OrderStatus.NeedsHuman, OrderStatus.Rejected, Actor.Machine));

Check("a person may reject it", () =>
    OrderLifecycle.CanMove(OrderStatus.NeedsHuman, OrderStatus.Rejected, Actor.Merchant));

Check("a person may confirm it", () =>
    OrderLifecycle.CanMove(OrderStatus.NeedsHuman, OrderStatus.Confirmed, Actor.Merchant));

// A thumb lands on the wrong key often enough that this has to be recoverable.
Check("the merchant can undo a rejection", () =>
    OrderLifecycle.CanMove(OrderStatus.Rejected, OrderStatus.Confirmed, Actor.Merchant));

Check("but the machine cannot", () =>
    !OrderLifecycle.CanMove(OrderStatus.Rejected, OrderStatus.Confirmed, Actor.Machine));

Check("nor can the shop", () =>
    !OrderLifecycle.CanMove(OrderStatus.Rejected, OrderStatus.Confirmed, Actor.Shop));

Check("an unpaid order can be cancelled by anyone", () =>
    OrderLifecycle.CanMove(OrderStatus.AwaitingPayment, OrderStatus.Cancelled, Actor.Shop)
    && OrderLifecycle.CanMove(OrderStatus.NeedsHuman, OrderStatus.Cancelled, Actor.Merchant));

// Once the goods have left, cancelling here would tell the shop something untrue.
Check("a shipped order cannot be cancelled", () =>
    !OrderLifecycle.CanMove(OrderStatus.Shipped, OrderStatus.Cancelled, Actor.Merchant));

Check("a delivered order cannot be cancelled", () =>
    !OrderLifecycle.CanMove(OrderStatus.Delivered, OrderStatus.Cancelled, Actor.Merchant));

Check("a cancelled order is finished", () =>
    OrderLifecycle.IsClosed(OrderStatus.Cancelled)
    && OrderLifecycle.IsClosed(OrderStatus.Delivered)
    && OrderLifecycle.IsOpen(OrderStatus.NeedsHuman));

Check("refusals are sentences, not codes", () =>
{
    var r = OrderLifecycle.Refuse(OrderStatus.Shipped, OrderStatus.Cancelled, Actor.Merchant);
    return r is not null && r.Contains("shipped", StringComparison.OrdinalIgnoreCase);
});

Check("an allowed move gives no refusal", () =>
    OrderLifecycle.Refuse(OrderStatus.Calling, OrderStatus.Confirmed, Actor.Machine) is null);

// ---------------------------------------------------------------------------
// The pipeline follows what the merchant bought
// ---------------------------------------------------------------------------
Subscription Sub(bool calls = false, bool pay = false, bool courier = false) =>
    new() { Calls = calls, Payments = pay, Courier = courier };

Check("calls on: a new order starts ringing", () =>
    OrderLifecycle.FirstStep(Sub(calls: true, pay: true)) == OrderStatus.Calling);

// A merchant who never bought the call service must not be rung by a code path that
// forgot to check - so the skip is a property of the data, not an if-statement.
Check("payments only: no call, straight to waiting for money", () =>
    OrderLifecycle.FirstStep(Sub(pay: true)) == OrderStatus.AwaitingPayment);

Check("nothing bought: the order is simply confirmed", () =>
    OrderLifecycle.FirstStep(Sub()) == OrderStatus.Confirmed);

Check("after confirming, money comes before courier", () =>
    OrderLifecycle.AfterConfirmed(Sub(pay: true, courier: true)) == OrderStatus.AwaitingPayment);

Check("with no payments, confirming goes straight to the courier", () =>
    OrderLifecycle.AfterConfirmed(Sub(courier: true)) == OrderStatus.Booked);

Check("with neither, confirming is the end of the pipeline", () =>
    OrderLifecycle.AfterConfirmed(Sub()) is null);

Check("a call outcome becomes a status in exactly one place", () =>
    OrderLifecycle.FromCallOutcome(CallOutcome.Confirmed) == OrderStatus.Confirmed
    && OrderLifecycle.FromCallOutcome(CallOutcome.Rejected) == OrderStatus.Rejected
    && OrderLifecycle.FromCallOutcome(CallOutcome.NoAnswer) == OrderStatus.NeedsHuman
    && OrderLifecycle.FromCallOutcome(CallOutcome.Failed) == OrderStatus.NeedsHuman
    && OrderLifecycle.FromCallOutcome(CallOutcome.Unreachable) == OrderStatus.NeedsHuman);

// ---------------------------------------------------------------------------
// Taking an order in
// ---------------------------------------------------------------------------
var merchantId = Guid.Parse("01929999-0000-7000-8000-000000000001");
var now = new DateTimeOffset(2026, 9, 28, 10, 0, 0, TimeSpan.Zero);

(MemoryDatabase Db, CreateOrderService Create, OrderTransitionService Move) Harness(
    bool calls = true, bool pay = true, bool courier = false, bool webhook = true)
{
    var db = new MemoryDatabase();

    db.Merchants[merchantId] = new Merchant
    { Id = merchantId, TenantId = merchantId, Name = "টেস্ট স্টোর", Msisdn = "8801711111111" };

    db.Subscriptions[merchantId] = new Subscription
    { TenantId = merchantId, Calls = calls, Payments = pay, Courier = courier };

    if (webhook)
    {
        db.Webhooks[merchantId] = new WebhookEndpoint
        {
            TenantId = merchantId, Url = "https://shop.example.com/hook",
            SecretCipher = "s", KeyRingId = "dev", Active = true,
        };
    }

    var clock = new FixedClock(now);
    var orders = new MemoryOrderStore(db);
    var merchants = new MemoryMerchantStore(db);

    return (db, new CreateOrderService(orders, merchants, clock),
        new OrderTransitionService(orders, merchants, clock));
}

(MemoryDatabase Db, OrderWorkflowService Workflow, IWorkflowSessionStore Sessions) WorkflowHarness(
    bool calls = true, bool pay = true, bool courier = false, bool webhook = true)
{
    var h = Harness(calls: calls, pay: pay, courier: courier, webhook: webhook);
    var orders = new MemoryOrderStore(h.Db);
    var merchants = new MemoryMerchantStore(h.Db);
    var sessions = new MemoryWorkflowSessionStore(h.Db);
    var clock = new FixedClock(now);

    return (h.Db, new OrderWorkflowService(h.Create, h.Move, orders, merchants, sessions, clock), sessions);
}

(MemoryDatabase Db, CallOutcomeService Outcomes) CallOutcomeHarness(
    DateTimeOffset? at = null, bool webhook = true)
{
    var h = Harness(calls: true, pay: false, webhook: webhook);
    var orders = new MemoryOrderStore(h.Db);
    var merchants = new MemoryMerchantStore(h.Db);
    var sessions = new MemoryWorkflowSessionStore(h.Db);
    var clock = new FixedClock(at ?? now);
    var workflow = new OrderWorkflowService(h.Create, h.Move, orders, merchants, sessions, clock);

    return (h.Db, new CallOutcomeService(orders, workflow, clock));
}

(MemoryDatabase Db, WorkflowActionHandlers Actions, OrderWorkflowService Workflow) ActionHarness(
    bool calls = false, bool pay = true, bool courier = true, bool webhook = true)
{
    var h = Harness(calls: calls, pay: pay, courier: courier, webhook: webhook);
    var orders = new MemoryOrderStore(h.Db);
    var invoices = new MemoryInvoiceStore(h.Db);
    var actionStore = new MemoryWorkflowActionStore(h.Db);
    var workflowSessions = new MemoryWorkflowSessionStore(h.Db);
    var merchants = new MemoryMerchantStore(h.Db);
    var clock = new FixedClock(now);
    var workflow = new OrderWorkflowService(h.Create, h.Move, orders, merchants, workflowSessions, clock);

    return (h.Db, new WorkflowActionHandlers(actionStore, orders, invoices, h.Move, clock), workflow);
}

(MemoryDatabase Db, AiProposalGate Gate, OrderWorkflowService Workflow) AiGateHarness(
    bool calls = true, bool pay = false, bool courier = false, bool webhook = true)
{
    var h = Harness(calls: calls, pay: pay, courier: courier, webhook: webhook);
    var orders = new MemoryOrderStore(h.Db);
    var merchants = new MemoryMerchantStore(h.Db);
    var sessions = new MemoryWorkflowSessionStore(h.Db);
    var clock = new FixedClock(now);
    var workflow = new OrderWorkflowService(h.Create, h.Move, orders, merchants, sessions, clock);

    return (h.Db, new AiProposalGate(orders, workflow), workflow);
}

(MemoryDatabase Db, VoiceAiDecisionService Decisions, FakeConversationModel Model,
    OrderWorkflowService Workflow) VoiceAiHarness(
    string response,
    bool calls = true,
    bool pay = false,
    bool courier = false,
    bool webhook = true)
{
    var h = AiGateHarness(calls: calls, pay: pay, courier: courier, webhook: webhook);
    var model = new FakeConversationModel(response);
    var handoff = new HandoffContextService(
        new MemoryOrderStore(h.Db),
        new DeterministicHandoffContextSummarizer(),
        new FixedClock(now));

    return (h.Db, new VoiceAiDecisionService(model, h.Gate, handoff), model, h.Workflow);
}

VoiceAiDecisionCommand VoiceDecision(Guid orderId, string transcript) => new()
{
    MerchantId = merchantId,
    OrderId = orderId,
    CallSessionId = Guid.CreateVersion7(),
    Transcript =
    [
        new Transcript(transcript, true, 0.95, now),
    ],
};

(MemoryDatabase Db, HandoffContextService Handoff, ReviewQueueService Reviews,
    OrderWorkflowService Workflow) HandoffHarness(
    DateTimeOffset? at = null,
    bool calls = true,
    bool pay = false,
    bool courier = false,
    bool webhook = true)
{
    var h = ReviewHarness(at: at, calls: calls, pay: pay, courier: courier, webhook: webhook);
    var orders = new MemoryOrderStore(h.Db);
    var clock = new FixedClock(at ?? now);

    return (h.Db,
        new HandoffContextService(orders, new DeterministicHandoffContextSummarizer(), clock),
        h.Reviews,
        h.Workflow);
}

(MemoryDatabase Db, CallRecordingService Recordings, FakeRecordingArchive Archive,
    OrderWorkflowService Workflow) RecordingHarness(
    DateTimeOffset? at = null,
    bool calls = true,
    bool pay = false,
    bool courier = false,
    bool webhook = true)
{
    var h = AiGateHarness(calls: calls, pay: pay, courier: courier, webhook: webhook);
    var orders = new MemoryOrderStore(h.Db);
    var clock = new FixedClock(at ?? now);
    var archive = new FakeRecordingArchive();
    var signer = new RecordingPlaybackUrlSigner("recording-test-secret");

    return (h.Db, new CallRecordingService(orders, archive, signer, clock), archive, h.Workflow);
}

Dictionary<string, string> Query(string url)
{
    var question = url.IndexOf('?', StringComparison.Ordinal);
    var pieces = question < 0 ? Array.Empty<string>() : url[(question + 1)..].Split('&');
    var parsed = new Dictionary<string, string>(StringComparer.Ordinal);

    foreach (var piece in pieces)
    {
        var equals = piece.IndexOf('=', StringComparison.Ordinal);
        if (equals <= 0) continue;

        parsed[Uri.UnescapeDataString(piece[..equals])] =
            Uri.UnescapeDataString(piece[(equals + 1)..]);
    }

    return parsed;
}

(MemoryDatabase Db, ReviewQueueService Reviews, OrderWorkflowService Workflow) ReviewHarness(
    DateTimeOffset? at = null,
    bool calls = true,
    bool pay = false,
    bool courier = false,
    bool webhook = true)
{
    var h = Harness(calls: calls, pay: pay, courier: courier, webhook: webhook);
    var orders = new MemoryOrderStore(h.Db);
    var merchants = new MemoryMerchantStore(h.Db);
    var sessions = new MemoryWorkflowSessionStore(h.Db);
    var clock = new FixedClock(at ?? now);
    var workflow = new OrderWorkflowService(h.Create, h.Move, orders, merchants, sessions, clock);

    return (h.Db, new ReviewQueueService(orders, workflow, clock), workflow);
}

(MemoryDatabase Db, AppAuthService Auth, FixedClock Clock) AppAuthHarness(DateTimeOffset? at = null)
{
    var db = new MemoryDatabase();
    db.Merchants[merchantId] = new Merchant
    {
        Id = merchantId,
        TenantId = merchantId,
        Name = "Test Store",
        Msisdn = "8801711111111",
        TimeZone = "Asia/Dhaka",
    };
    db.Subscriptions[merchantId] = new Subscription
    {
        TenantId = merchantId,
        Calls = true,
        Payments = true,
        Courier = false,
        Plan = "trial",
    };

    var clock = new FixedClock(at ?? now);
    var auth = new AppAuthService(
        new MemoryMerchantStore(db),
        new MemoryAppAuthStore(db),
        new MemoryRateLimitStore(db),
        clock);

    return (db, auth, clock);
}

(MemoryDatabase Db, AppAuthService Auth, AppBootstrapService Bootstrap) BootstrapHarness()
{
    var h = AppAuthHarness();
    h.Db.Webhooks[merchantId] = new WebhookEndpoint
    {
        TenantId = merchantId,
        Url = "https://shop.example.com/hook",
        SecretCipher = "s",
        KeyRingId = "dev",
        Active = true,
    };
    h.Db.Credentials["mp_app_boot"] = new ApiCredential
    {
        TenantId = merchantId,
        KeyId = "mp_app_boot",
        SecretCipher = "s",
        KeyRingId = "dev",
        Label = "app bootstrap",
    };

    return (h.Db, h.Auth,
        new AppBootstrapService(new MemoryMerchantStore(h.Db), new MemoryOrderStore(h.Db)));
}

(MemoryDatabase Db, AppAuthService Auth, AppHomeService Home) HomeHarness(DateTimeOffset? at = null)
{
    var h = AppAuthHarness(at);

    return (h.Db, h.Auth, new AppHomeService(new MemoryOrderStore(h.Db), h.Clock));
}

(MemoryDatabase Db, AppAuthService Auth, AppOrderService Orders) AppOrderHarness(DateTimeOffset? at = null)
{
    var h = AppAuthHarness(at);

    return (h.Db, h.Auth, new AppOrderService(new MemoryOrderStore(h.Db)));
}

(MemoryDatabase Db, AppOrderActionService Actions, AppOrderService Orders) AppActionHarness(
    DateTimeOffset? at = null,
    bool calls = true,
    bool pay = false,
    bool courier = false)
{
    var h = AppAuthHarness(at);
    h.Db.Subscriptions[merchantId] = new Subscription
    {
        TenantId = merchantId,
        Calls = calls,
        Payments = pay,
        Courier = courier,
        Plan = "trial",
    };

    var clock = new FixedClock(at ?? now);
    var orderStore = new MemoryOrderStore(h.Db);
    var merchants = new MemoryMerchantStore(h.Db);
    var create = new CreateOrderService(orderStore, merchants, clock);
    var move = new OrderTransitionService(orderStore, merchants, clock);
    var sessions = new MemoryWorkflowSessionStore(h.Db);
    var workflow = new OrderWorkflowService(create, move, orderStore, merchants, sessions, clock);
    var appOrders = new AppOrderService(orderStore);
    var reviews = new ReviewQueueService(orderStore, workflow, clock);

    return (h.Db, new AppOrderActionService(appOrders, orderStore, workflow, reviews), appOrders);
}

(MemoryDatabase Db, AppSettingsService Settings) AppSettingsHarness(
    bool calls = true,
    bool pay = true,
    bool courier = false)
{
    var h = AppAuthHarness();
    h.Db.Subscriptions[merchantId] = new Subscription
    {
        TenantId = merchantId,
        Calls = calls,
        Payments = pay,
        Courier = courier,
        Plan = "trial",
    };
    h.Db.Credentials["mp_settings"] = new ApiCredential
    {
        TenantId = merchantId,
        KeyId = "mp_settings",
        SecretCipher = "sealed:s",
        KeyRingId = PrefixProtector.KeyRing,
        Label = "settings",
    };
    h.Db.Webhooks[merchantId] = new WebhookEndpoint
    {
        TenantId = merchantId,
        Url = "https://shop.example.com/hook",
        SecretCipher = "sealed:s",
        KeyRingId = PrefixProtector.KeyRing,
        Active = true,
    };

    var merchants = new MemoryMerchantStore(h.Db);
    var webhooks = new WebhookService(
        merchants, new PrefixProtector(), new RecordingWebhookSender(true), h.Clock);

    return (h.Db, new AppSettingsService(merchants, webhooks, h.Clock));
}

(MemoryDatabase Db, AppDevicesService Devices, FixedClock Clock) AppDeviceHarness(DateTimeOffset? at = null)
{
    var h = AppAuthHarness(at);

    return (h.Db, new AppDevicesService(new MemoryAppDeviceStore(h.Db), h.Clock), h.Clock);
}

(MemoryDatabase Db, AppDevicesService Devices, DeviceOfflineAlertService OfflineAlerts,
    FixedClock Clock) DeviceOfflineHarness(DateTimeOffset? at = null)
{
    var h = AppDeviceHarness(at);
    var store = new MemoryAppDeviceStore(h.Db);

    return (h.Db, h.Devices,
        new DeviceOfflineAlertService(store, new MemoryMerchantStore(h.Db), h.Clock),
        h.Clock);
}

(MemoryDatabase Db, AppDevicesService Devices, RawEventIngestService Ingest, FixedClock Clock)
    RawIngestHarness(DateTimeOffset? at = null)
{
    var h = AppDeviceHarness(at);

    return (h.Db, h.Devices, new RawEventIngestService(new MemoryRawEventStore(h.Db), h.Clock),
        h.Clock);
}

(MemoryDatabase Db, WorkflowActionHandlers Actions, OrderWorkflowService Workflow,
    RawEventIngestService Ingest, PaymentPipeline Pipeline, PaymentReviewService PaymentReviews)
    PaymentPipelineHarness()
{
    var h = ActionHarness();
    var rawEvents = new MemoryRawEventStore(h.Db);
    var invoices = new MemoryInvoiceStore(h.Db);
    var orders = new MemoryOrderStore(h.Db);
    var merchants = new MemoryMerchantStore(h.Db);
    var clock = new FixedClock(now);
    var transitions = new OrderTransitionService(orders, merchants, clock);

    return (h.Db, h.Actions, h.Workflow, new RawEventIngestService(rawEvents, clock),
        new PaymentPipeline(rawEvents, invoices, orders, transitions, MessageParser.ForBkash(), clock),
        new PaymentReviewService(rawEvents, invoices, orders, transitions, MessageParser.ForBkash(), clock));
}

(MemoryDatabase Db, AppDevicesService Devices, WorkflowActionHandlers Actions,
    OrderWorkflowService Workflow, RawEventIngestService Ingest, PaymentPipeline Pipeline,
    PaymentReviewService PaymentReviews, DeviceOfflineAlertService OfflineAlerts, FixedClock Clock)
    PaymentE2eHarness()
{
    var db = new MemoryDatabase();
    db.Merchants[merchantId] = new Merchant
    {
        Id = merchantId,
        TenantId = merchantId,
        Name = "Test Store",
        Msisdn = "8801711111111",
        TimeZone = "Asia/Dhaka",
    };
    db.Subscriptions[merchantId] = new Subscription
    {
        TenantId = merchantId,
        Calls = false,
        Payments = true,
        Courier = false,
        Plan = "trial",
    };
    db.Webhooks[merchantId] = new WebhookEndpoint
    {
        TenantId = merchantId,
        Url = "https://shop.example.com/hook",
        SecretCipher = "s",
        KeyRingId = "dev",
        Active = true,
    };

    var clock = new FixedClock(now);
    var merchants = new MemoryMerchantStore(db);
    var orders = new MemoryOrderStore(db);
    var invoices = new MemoryInvoiceStore(db);
    var devices = new MemoryAppDeviceStore(db);
    var rawEvents = new MemoryRawEventStore(db);
    var actionStore = new MemoryWorkflowActionStore(db);
    var sessions = new MemoryWorkflowSessionStore(db);
    var create = new CreateOrderService(orders, merchants, clock);
    var transitions = new OrderTransitionService(orders, merchants, clock);
    var workflow = new OrderWorkflowService(create, transitions, orders, merchants, sessions, clock);

    return (db,
        new AppDevicesService(devices, clock),
        new WorkflowActionHandlers(actionStore, orders, invoices, transitions, clock),
        workflow,
        new RawEventIngestService(rawEvents, clock),
        new PaymentPipeline(rawEvents, invoices, orders, transitions, MessageParser.ForBkash(), clock),
        new PaymentReviewService(rawEvents, invoices, orders, transitions, MessageParser.ForBkash(), clock),
        new DeviceOfflineAlertService(devices, merchants, clock),
        clock);
}

Order AddOrder(MemoryDatabase db, string reference, OrderStatus status, DateTimeOffset createdAt,
    string customer = "Customer", string msisdn = "8801711111111", Guid? tenant = null)
{
    var order = new Order
    {
        Id = Guid.CreateVersion7(),
        TenantId = tenant ?? merchantId,
        Reference = reference,
        CustomerName = customer,
        Msisdn = msisdn,
        Amount = 100,
        Summary = "shirt",
        Status = status,
        CreatedAt = createdAt,
        UpdatedAt = createdAt,
    };
    db.Orders[order.Id] = order;

    return order;
}

void AddEvent(MemoryDatabase db, OrderStatus? to, string type, DateTimeOffset at, Guid? tenant = null)
{
    var orderId = Guid.CreateVersion7();
    db.Orders[orderId] = new Order
    {
        Id = orderId,
        TenantId = tenant ?? merchantId,
        Reference = $"EV-{db.Orders.Count + 1}",
        CustomerName = "Home",
        Msisdn = "8801711111111",
        Amount = 100,
        Status = to ?? OrderStatus.Calling,
        CreatedAt = at,
        UpdatedAt = at,
    };
    db.Events.Add(new OrderEvent
    {
        TenantId = tenant ?? merchantId,
        OrderId = orderId,
        Type = type,
        Actor = Actor.Machine,
        To = to,
        At = at,
    });
}

CreateOrderCommand Cmd(string reference = "ORD-1", string msisdn = "01711223344", decimal amount = 1250m)
    => new()
    {
        MerchantId = merchantId, Reference = reference, Msisdn = msisdn,
        CustomerName = "সাদিয়া আক্তার", Amount = amount, Summary = "দুইটি শার্ট",
    };

// ---------------------------------------------------------------------------
// App auth
// ---------------------------------------------------------------------------
await CheckAsync("app login issues access and refresh tokens from an OTP", async () =>
{
    var h = AppAuthHarness();
    var otp = await h.Auth.RequestOtpAsync("01711111111");
    var token = await h.Auth.VerifyOtpAsync("01711111111", otp.DevOtp);
    var principal = await h.Auth.ValidateAccessAsync(token.AccessToken);

    return otp.Outcome == RequestOtpOutcome.Sent
        && token.Outcome == AppTokenOutcome.Issued
        && token.AccessToken is not null
        && token.RefreshToken is not null
        && principal?.MerchantId == merchantId
        && h.Db.AppTokens.Values.Count(t => t.Kind == AppTokenKind.Refresh) == 1;
});

await CheckAsync("OTP brute force is limited", async () =>
{
    var h = AppAuthHarness();
    await h.Auth.RequestOtpAsync("01711111111");

    AppTokenResult last = null!;
    for (var i = 0; i < 6; i++)
    {
        last = await h.Auth.VerifyOtpAsync("01711111111", "000000");
    }

    return last.Outcome == AppTokenOutcome.RateLimited;
});

await CheckAsync("OTP requests are rate limited", async () =>
{
    var h = AppAuthHarness();
    var first = await h.Auth.RequestOtpAsync("01711111111");
    await h.Auth.RequestOtpAsync("01711111111");
    await h.Auth.RequestOtpAsync("01711111111");
    var fourth = await h.Auth.RequestOtpAsync("01711111111");

    return first.Outcome == RequestOtpOutcome.Sent
        && fourth.Outcome == RequestOtpOutcome.RateLimited
        && h.Db.AppOtps.Count == 3;
});

await CheckAsync("refresh rotation invalidates the old refresh token", async () =>
{
    var h = AppAuthHarness();
    var otp = await h.Auth.RequestOtpAsync("01711111111");
    var token = await h.Auth.VerifyOtpAsync("01711111111", otp.DevOtp);
    var rotated = await h.Auth.RefreshAsync(token.RefreshToken);
    var replay = await h.Auth.RefreshAsync(token.RefreshToken);
    var secondUse = await h.Auth.RefreshAsync(rotated.RefreshToken);

    return rotated.Outcome == AppTokenOutcome.Issued
        && replay.Outcome == AppTokenOutcome.Invalid
        && secondUse.Outcome == AppTokenOutcome.Issued
        && h.Db.AppTokens.Values.Count(t => t.Kind == AppTokenKind.Refresh) == 3;
});

await CheckAsync("logout revokes the refresh token", async () =>
{
    var h = AppAuthHarness();
    var otp = await h.Auth.RequestOtpAsync("01711111111");
    var token = await h.Auth.VerifyOtpAsync("01711111111", otp.DevOtp);

    await h.Auth.LogoutAsync(token.RefreshToken);
    var afterLogout = await h.Auth.RefreshAsync(token.RefreshToken);

    return afterLogout.Outcome == AppTokenOutcome.Invalid;
});

await CheckAsync("rate limit buckets reset after the window", async () =>
{
    var db = new MemoryDatabase();
    var limits = new MemoryRateLimitStore(db);

    var one = await limits.TryConsumeAsync("shop:k1", now, TimeSpan.FromMinutes(1), 2);
    var two = await limits.TryConsumeAsync("shop:k1", now.AddSeconds(1), TimeSpan.FromMinutes(1), 2);
    var blocked = await limits.TryConsumeAsync("shop:k1", now.AddSeconds(2), TimeSpan.FromMinutes(1), 2);
    var reset = await limits.TryConsumeAsync("shop:k1", now.AddSeconds(61), TimeSpan.FromMinutes(1), 2);

    return one && two && !blocked && reset;
});

await CheckAsync("app bootstrap returns merchant services settings and counters", async () =>
{
    var h = BootstrapHarness();
    var otp = await h.Auth.RequestOtpAsync("01711111111");
    var token = await h.Auth.VerifyOtpAsync("01711111111", otp.DevOtp);
    var principal = await h.Auth.ValidateAccessAsync(token.AccessToken);

    h.Db.Orders[Guid.CreateVersion7()] = new Order
    {
        TenantId = merchantId,
        Reference = "BOOT-1",
        CustomerName = "A",
        Msisdn = "8801711111111",
        Amount = 100,
        Status = OrderStatus.NeedsHuman,
    };
    h.Db.Orders[Guid.CreateVersion7()] = new Order
    {
        TenantId = merchantId,
        Reference = "BOOT-2",
        CustomerName = "B",
        Msisdn = "8801711111112",
        Amount = 200,
        Status = OrderStatus.AwaitingPayment,
    };

    var result = await h.Bootstrap.GetAsync(principal!.MerchantId);

    return result.Found
        && result.Bootstrap!.Merchant.Id == merchantId
        && result.Bootstrap.EnabledServices.Calls
        && result.Bootstrap.EnabledServices.Payments
        && !result.Bootstrap.EnabledServices.Courier
        && result.Bootstrap.Settings.WebhookConfigured
        && result.Bootstrap.Settings.WebhookActive
        && result.Bootstrap.Settings.ActiveApiKeys == 1
        && result.Bootstrap.Counters.NeedsHuman == 1
        && result.Bootstrap.Counters.AwaitingPayment == 1
        && result.Bootstrap.Counters.Open == 2;
});

await CheckAsync("app bootstrap refuses an invalid access token", async () =>
{
    var h = BootstrapHarness();
    var principal = await h.Auth.ValidateAccessAsync("not-a-real-token");

    return principal is null;
});

await CheckAsync("home numbers split today and seven-day counts", async () =>
{
    var homeNow = new DateTimeOffset(2026, 9, 28, 15, 30, 0, TimeSpan.Zero);
    var h = HomeHarness(homeNow);

    AddEvent(h.Db, OrderStatus.Received, "order.received", homeNow.AddHours(-1));
    AddEvent(h.Db, OrderStatus.Confirmed, "order.confirmed", homeNow.AddHours(-2));
    AddEvent(h.Db, OrderStatus.NeedsHuman, "order.needs_human", homeNow.AddDays(-2));
    AddEvent(h.Db, OrderStatus.Paid, "order.paid", homeNow.AddDays(-3));
    AddEvent(h.Db, OrderStatus.Shipped, "order.shipped", homeNow.AddDays(-6));
    AddEvent(h.Db, null, "call.failed", homeNow.AddDays(-1));
    AddEvent(h.Db, OrderStatus.Confirmed, "order.confirmed", homeNow.AddDays(-8));

    var numbers = await h.Home.NumbersAsync(merchantId);

    return numbers.Today.New == 1
        && numbers.Today.Confirmed == 1
        && numbers.Today.NeedsHuman == 0
        && numbers.Today.Paid == 0
        && numbers.Today.Shipped == 0
        && numbers.Today.FailedCalls == 0
        && numbers.SevenDays.New == 1
        && numbers.SevenDays.Confirmed == 1
        && numbers.SevenDays.NeedsHuman == 1
        && numbers.SevenDays.Paid == 1
        && numbers.SevenDays.Shipped == 1
        && numbers.SevenDays.FailedCalls == 1
        && numbers.TodayStart == new DateTimeOffset(2026, 9, 28, 0, 0, 0, TimeSpan.Zero)
        && numbers.SevenDayStart == new DateTimeOffset(2026, 9, 22, 0, 0, 0, TimeSpan.Zero);
});

await CheckAsync("home numbers stay inside one merchant", async () =>
{
    var homeNow = new DateTimeOffset(2026, 9, 28, 15, 30, 0, TimeSpan.Zero);
    var h = HomeHarness(homeNow);
    var otherMerchant = Guid.CreateVersion7();

    AddEvent(h.Db, OrderStatus.Confirmed, "order.confirmed", homeNow.AddHours(-1));
    AddEvent(h.Db, OrderStatus.Confirmed, "order.confirmed", homeNow.AddHours(-1), otherMerchant);

    var numbers = await h.Home.NumbersAsync(merchantId);

    return numbers.Today.Confirmed == 1 && numbers.SevenDays.Confirmed == 1;
});

await CheckAsync("app order list filters by status date and search", async () =>
{
    var h = AppOrderHarness();
    AddOrder(h.Db, "ORD-A", OrderStatus.Confirmed, now.AddDays(-1), customer: "Sadia");
    AddOrder(h.Db, "ORD-B", OrderStatus.NeedsHuman, now.AddDays(-1), customer: "Sadia");
    AddOrder(h.Db, "OLD-A", OrderStatus.Confirmed, now.AddDays(-20), customer: "Sadia");
    AddOrder(h.Db, "ORD-C", OrderStatus.Confirmed, now.AddDays(-1), customer: "Karim");

    var page = await h.Orders.ListAsync(merchantId, new AppOrderListQuery
    {
        Status = OrderStatus.Confirmed,
        Search = "sadia",
        From = now.AddDays(-7),
        To = now,
    });

    return page.Items.Count == 1 && page.Items[0].Reference == "ORD-A";
});

await CheckAsync("app order list pages by before cursor", async () =>
{
    var h = AppOrderHarness();
    AddOrder(h.Db, "ORD-1", OrderStatus.Confirmed, now.AddMinutes(-1));
    AddOrder(h.Db, "ORD-2", OrderStatus.Confirmed, now.AddMinutes(-2));
    AddOrder(h.Db, "ORD-3", OrderStatus.Confirmed, now.AddMinutes(-3));

    var first = await h.Orders.ListAsync(merchantId, new AppOrderListQuery { Take = 2 });
    var second = await h.Orders.ListAsync(merchantId, new AppOrderListQuery
    {
        Take = 2,
        Before = first.NextBefore,
    });

    return first.Items.Select(i => i.Reference).SequenceEqual(["ORD-1", "ORD-2"])
        && first.NextBefore == first.Items[^1].CreatedAt
        && second.Items.Count == 1
        && second.Items[0].Reference == "ORD-3"
        && second.NextBefore is null;
});

await CheckAsync("app order detail and timeline stay inside merchant", async () =>
{
    var h = AppOrderHarness();
    var mine = AddOrder(h.Db, "ORD-MINE", OrderStatus.NeedsHuman, now.AddMinutes(-1));
    var otherMerchant = Guid.CreateVersion7();
    AddOrder(h.Db, "ORD-OTHER", OrderStatus.Confirmed, now.AddMinutes(-1), tenant: otherMerchant);
    h.Db.Events.Add(new OrderEvent
    {
        TenantId = merchantId,
        OrderId = mine.Id,
        Type = "order.received",
        Actor = Actor.Shop,
        To = OrderStatus.Calling,
        At = now.AddMinutes(-1),
    });
    h.Db.Events.Add(new OrderEvent
    {
        TenantId = merchantId,
        OrderId = mine.Id,
        Type = "order.needs_human",
        Actor = Actor.Machine,
        From = OrderStatus.Calling,
        To = OrderStatus.NeedsHuman,
        Detail = "no answer",
        At = now,
    });

    var detail = await h.Orders.DetailAsync(merchantId, "ORD-MINE");
    var hidden = await h.Orders.DetailAsync(merchantId, "ORD-OTHER");
    var timeline = await h.Orders.TimelineAsync(merchantId, "ORD-MINE");

    return detail?.Reference == "ORD-MINE"
        && hidden is null
        && timeline.Count == 2
        && timeline[0].Type == "order.received"
        && timeline[1].Detail == "no answer";
});

await CheckAsync("app decision confirms through the workflow", async () =>
{
    var h = AppActionHarness(pay: true);
    AddOrder(h.Db, "ACT-1", OrderStatus.NeedsHuman, now.AddMinutes(-1));

    var result = await h.Actions.DecideAsync(
        merchantId, "ACT-1", AppDecision.Confirm, "sadia", "customer confirmed");

    return result.Outcome == AppOrderActionOutcome.Moved
        && result.Order!.Status == OrderStatus.AwaitingPayment
        && h.Db.Events.Any(e => e.Type == "order.confirmed" && e.ActorName == "sadia")
        && h.Db.Events.Any(e => e.Type == "workflow.awaitingpayment");
});

await CheckAsync("app decision rejects only as a merchant action", async () =>
{
    var h = AppActionHarness();
    AddOrder(h.Db, "ACT-2", OrderStatus.NeedsHuman, now.AddMinutes(-1));

    var result = await h.Actions.DecideAsync(
        merchantId, "ACT-2", AppDecision.Reject, "sadia", "customer said no");

    return result.Outcome == AppOrderActionOutcome.Moved
        && result.Order!.Status == OrderStatus.Rejected
        && h.Db.Events.Any(e =>
            e.Type == "order.rejected" && e.Actor == Actor.Merchant && e.ActorName == "sadia");
});

await CheckAsync("app recall reopens a rejected order through confirmation", async () =>
{
    var h = AppActionHarness(pay: true);
    AddOrder(h.Db, "ACT-3", OrderStatus.Rejected, now.AddMinutes(-1));

    var result = await h.Actions.RecallAsync(
        merchantId, "ACT-3", "sadia", "wrong keypress");

    return result.Outcome == AppOrderActionOutcome.Moved
        && result.Order!.Status == OrderStatus.AwaitingPayment
        && h.Db.Events.Any(e => e.Type == "order.recalled" && e.From == OrderStatus.Rejected);
});

await CheckAsync("app review claim uses the review queue rules", async () =>
{
    var h = AppActionHarness();
    AddOrder(h.Db, "ACT-4", OrderStatus.NeedsHuman, now.AddMinutes(-1));

    var first = await h.Actions.ClaimReviewAsync(
        merchantId, "ACT-4", "rafi", TimeSpan.FromMinutes(5));
    var second = await h.Actions.ClaimReviewAsync(
        merchantId, "ACT-4", "sadia", TimeSpan.FromMinutes(5));

    return first.Outcome == ReviewClaimOutcome.Claimed
        && first.Order!.ClaimedBy == "rafi"
        && second.Outcome == ReviewClaimOutcome.AlreadyClaimed;
});

await CheckAsync("app marks a booked order as shipped through the workflow", async () =>
{
    var h = AppActionHarness();
    AddOrder(h.Db, "ACT-5", OrderStatus.Booked, now.AddMinutes(-1));

    var result = await h.Actions.MarkShippedAsync(
        merchantId, "ACT-5", "sadia", "handed to courier");

    return result.Outcome == AppOrderActionOutcome.Moved
        && result.Order!.Status == OrderStatus.Shipped
        && result.Order.ShippedAt is not null
        && h.Db.Events.Any(e => e.Type == "order.shipped" && e.Actor == Actor.Merchant);
});

await CheckAsync("app refuses shipping before the order is booked", async () =>
{
    var h = AppActionHarness();
    AddOrder(h.Db, "ACT-6", OrderStatus.AwaitingPayment, now.AddMinutes(-1));

    var result = await h.Actions.MarkShippedAsync(
        merchantId, "ACT-6", "sadia", null);

    return result.Outcome == AppOrderActionOutcome.Refused
        && h.Db.Orders.Values.Single(o => o.Reference == "ACT-6").Status == OrderStatus.AwaitingPayment;
});

await CheckAsync("app settings expose only subscribed modules", async () =>
{
    var h = AppSettingsHarness(calls: true, pay: false, courier: true);

    var result = await h.Settings.GetAsync(merchantId);

    return result.Found
        && result.Settings!.Calls is not null
        && result.Settings.Payments is null
        && result.Settings.Courier is not null
        && result.Settings.ApiKeys.Count == 1
        && result.Settings.Webhook?.Active == true;
});

await CheckAsync("app settings update the merchant profile", async () =>
{
    var h = AppSettingsHarness();

    var result = await h.Settings.UpdateProfileAsync(merchantId, new UpdateProfileSettings(
        "Updated Shop", "Asia/Dhaka", "Mirpur", "01712223344"));

    var merchant = h.Db.Merchants[merchantId];

    return result.Found
        && result.Settings!.Profile.Name == "Updated Shop"
        && result.Settings.Profile.Address == "Mirpur"
        && result.Settings.Profile.SupportMsisdn == "8801712223344"
        && merchant.Name == "Updated Shop";
});

await CheckAsync("app settings update webhook through the webhook guard", async () =>
{
    var h = AppSettingsHarness();

    var updated = await h.Settings.UpdateWebhookAsync(
        merchantId, "https://shop.example.com/new-hook", active: false);
    var blocked = await h.Settings.UpdateWebhookAsync(
        merchantId, "https://127.0.0.1/hook", active: true);

    return updated.Outcome == WebhookOutcome.Updated
        && h.Db.Webhooks[merchantId].Url == "https://shop.example.com/new-hook"
        && !h.Db.Webhooks[merchantId].Active
        && blocked.Outcome == WebhookOutcome.Invalid;
});

await CheckAsync("app device pairing token is one-time and returns a device credential", async () =>
{
    var h = AppDeviceHarness();
    var issued = await h.Devices.CreatePairingTokenAsync(merchantId);
    var paired = await h.Devices.PairAsync(new PairDeviceCommand(
        issued.Token,
        "install-1",
        "Counter phone",
        "Pixel",
        "1.0.0",
        "push-a",
        DevicePermissionState.Healthy,
        89,
        "wifi"));
    var replay = await h.Devices.PairAsync(new PairDeviceCommand(
        issued.Token,
        "install-2",
        null,
        null,
        null,
        null,
        null,
        null,
        null));

    return paired.Outcome == PairDeviceOutcome.Paired
        && paired.DeviceToken is not null
        && paired.Device!.Fingerprint == "install-1"
        && paired.Device.HasPushToken
        && h.Db.AppDevices.Count == 1
        && h.Db.AppDevicePairingTokens.Values.Single().ConsumedAt == now
        && replay.Outcome == PairDeviceOutcome.Invalid;
});

await CheckAsync("expired app device pairing token cannot be consumed", async () =>
{
    var h = AppDeviceHarness();
    var issued = await h.Devices.CreatePairingTokenAsync(merchantId);
    var later = AppDeviceHarness(now.Add(AppDevicesService.PairingTtl).AddSeconds(1));
    later.Db.AppDevicePairingTokens[AppAuthService.Hash(issued.Token)] =
        h.Db.AppDevicePairingTokens.Values.Single();

    var paired = await later.Devices.PairAsync(new PairDeviceCommand(
        issued.Token,
        "late-install",
        null,
        null,
        null,
        null,
        null,
        null,
        null));

    return paired.Outcome == PairDeviceOutcome.Invalid && later.Db.AppDevices.IsEmpty;
});

await CheckAsync("app device heartbeat requires the device token and updates health", async () =>
{
    var h = AppDeviceHarness();
    var issued = await h.Devices.CreatePairingTokenAsync(merchantId);
    var paired = await h.Devices.PairAsync(new PairDeviceCommand(
        issued.Token,
        "install-1",
        null,
        null,
        "1.0.0",
        null,
        DevicePermissionState.Unknown,
        60,
        "wifi"));

    var refused = await h.Devices.HeartbeatAsync(
        paired.Device!.Id, "wrong", new DeviceHeartbeatCommand(
            DevicePermissionState.Healthy, 101, "4g", "1.1.0", "Galaxy"));
    var updated = await h.Devices.HeartbeatAsync(
        paired.Device.Id, paired.DeviceToken, new DeviceHeartbeatCommand(
            DevicePermissionState.Healthy, 101, "4g", "1.1.0", "Galaxy"));

    return refused.Outcome == DeviceUpdateOutcome.Unauthorized
        && updated.Outcome == DeviceUpdateOutcome.Updated
        && updated.Device!.PermissionState == DevicePermissionState.Healthy
        && updated.Device.BatteryPercent == 100
        && updated.Device.NetworkType == "4g"
        && updated.Device.AppVersion == "1.1.0";
});

await CheckAsync("app device push token update is stored but never returned", async () =>
{
    var h = AppDeviceHarness();
    var issued = await h.Devices.CreatePairingTokenAsync(merchantId);
    var paired = await h.Devices.PairAsync(new PairDeviceCommand(
        issued.Token,
        "install-1",
        null,
        null,
        null,
        null,
        null,
        null,
        null));

    var updated = await h.Devices.UpdatePushTokenAsync(
        paired.Device!.Id, paired.DeviceToken, "expo-token-1");

    return updated.Outcome == DeviceUpdateOutcome.Updated
        && updated.Device!.HasPushToken
        && h.Db.AppDevices[paired.Device.Id].PushToken == "expo-token-1"
        && updated.Device.ToString()!.Contains("expo-token-1", StringComparison.Ordinal) == false;
});

await CheckAsync("offline device queues one merchant alert", async () =>
{
    var h = DeviceOfflineHarness(now);
    h.Db.Webhooks[merchantId] = new WebhookEndpoint
    {
        TenantId = merchantId,
        Url = "https://shop.example.com/hook",
        SecretCipher = "s",
        KeyRingId = "dev",
        Active = true,
    };
    var issued = await h.Devices.CreatePairingTokenAsync(merchantId);
    var paired = await h.Devices.PairAsync(new PairDeviceCommand(
        issued.Token,
        "install-1",
        "Counter phone",
        "Galaxy",
        "1.0.0",
        null,
        DevicePermissionState.Healthy,
        80,
        "wifi"));

    h.Clock.Set(now.Add(DeviceOfflineAlertService.OfflineAfter).AddSeconds(1));
    var first = await h.OfflineAlerts.RunOnceAsync();
    var second = await h.OfflineAlerts.RunOnceAsync();

    return paired.Outcome == PairDeviceOutcome.Paired
        && first.Alerted == 1
        && second.Alerted == 0
        && h.Db.Outbox.Count == 1
        && h.Db.Outbox[0].EventType == "device.offline"
        && h.Db.Outbox[0].TenantId == merchantId
        && h.Db.Outbox[0].PayloadJson.Contains("\"deviceId\"", StringComparison.Ordinal)
        && h.Db.AppDevices[paired.Device!.Id].OfflineAlertedAt is not null;
});

await CheckAsync("offline alert clears after the device heartbeats again", async () =>
{
    var h = DeviceOfflineHarness(now);
    h.Db.Webhooks[merchantId] = new WebhookEndpoint
    {
        TenantId = merchantId,
        Url = "https://shop.example.com/hook",
        SecretCipher = "s",
        KeyRingId = "dev",
        Active = true,
    };
    var issued = await h.Devices.CreatePairingTokenAsync(merchantId);
    var paired = await h.Devices.PairAsync(new PairDeviceCommand(
        issued.Token,
        "install-1",
        null,
        null,
        null,
        null,
        null,
        null,
        null));

    h.Clock.Set(now.Add(DeviceOfflineAlertService.OfflineAfter).AddSeconds(1));
    await h.OfflineAlerts.RunOnceAsync();
    var recovered = await h.Devices.HeartbeatAsync(
        paired.Device!.Id, paired.DeviceToken, new DeviceHeartbeatCommand(
            DevicePermissionState.Healthy,
            70,
            "4g",
            null,
            null));

    return recovered.Outcome == DeviceUpdateOutcome.Updated
        && recovered.Device!.IsOffline == false
        && h.Db.AppDevices[paired.Device.Id].OfflineAlertedAt is null;
});

await CheckAsync("offline device without active webhook is not queued", async () =>
{
    var h = DeviceOfflineHarness(now);
    var issued = await h.Devices.CreatePairingTokenAsync(merchantId);
    await h.Devices.PairAsync(new PairDeviceCommand(
        issued.Token,
        "install-1",
        null,
        null,
        null,
        null,
        null,
        null,
        null));

    h.Clock.Set(now.Add(DeviceOfflineAlertService.OfflineAfter).AddSeconds(1));
    var result = await h.OfflineAlerts.RunOnceAsync();

    return result.Checked == 1
        && result.Alerted == 0
        && result.Skipped == 1
        && h.Db.Outbox.Count == 0;
});

await CheckAsync("app device list is scoped to one merchant", async () =>
{
    var h = AppDeviceHarness();
    var issued = await h.Devices.CreatePairingTokenAsync(merchantId);
    await h.Devices.PairAsync(new PairDeviceCommand(
        issued.Token,
        "mine",
        null,
        null,
        null,
        null,
        null,
        null,
        null));

    var otherMerchant = Guid.CreateVersion7();
    h.Db.AppDevices[Guid.CreateVersion7()] = new AppDevice
    {
        Id = Guid.CreateVersion7(),
        MerchantId = otherMerchant,
        DeviceTokenHash = "other",
        Fingerprint = "other",
        CreatedAt = now,
        UpdatedAt = now,
    };

    var mine = await h.Devices.ListAsync(merchantId);
    var theirs = await h.Devices.ListAsync(otherMerchant);

    return mine.Count == 1
        && mine[0].Fingerprint == "mine"
        && theirs.Count == 1
        && theirs[0].Fingerprint == "other";
});

await CheckAsync("paired app device ingests raw bKash events once", async () =>
{
    var h = RawIngestHarness();
    var issued = await h.Devices.CreatePairingTokenAsync(merchantId);
    var paired = await h.Devices.PairAsync(new PairDeviceCommand(
        issued.Token,
        "payments-phone",
        null,
        null,
        null,
        null,
        DevicePermissionState.Healthy,
        null,
        null));
    var device = await h.Devices.AuthenticateAsync(paired.Device!.Id, paired.DeviceToken);

    var first = await h.Ingest.IngestAsync(device!, [
        new RawDeviceEvent(
            EventSource.Notification,
            "bKash",
            "You have received Tk 200.00 from 01910126335. Fee Tk 0.00. Balance Tk 1,881.86. TrxID DHD6EYO3HO at 13/08/2026 16:02",
            now),
    ]);
    var replay = await h.Ingest.IngestAsync(device!, [
        new RawDeviceEvent(
            EventSource.Sms,
            " bKash ",
            "  You have received Tk 200.00 from 01910126335.\nFee Tk 0.00. Balance Tk 1,881.86. TrxID DHD6EYO3HO at 13/08/2026 16:02  ",
            now),
    ]);

    var row = h.Db.RawEventsByDedupeHash.Values.Single();

    return first == new IngestRawEventsResult(1, 0, 0)
        && replay == new IngestRawEventsResult(0, 1, 0)
        && row.MerchantId == merchantId
        && row.DeviceId == paired.Device.Id
        && row.SenderId == "bKash"
        && row.Body.StartsWith("You have received", StringComparison.Ordinal);
});

await CheckAsync("raw event ingest rejects untrusted sender and impossible device clocks", async () =>
{
    var h = RawIngestHarness();
    var issued = await h.Devices.CreatePairingTokenAsync(merchantId);
    var paired = await h.Devices.PairAsync(new PairDeviceCommand(
        issued.Token,
        "payments-phone",
        null,
        null,
        null,
        null,
        null,
        null,
        null));
    var device = await h.Devices.AuthenticateAsync(paired.Device!.Id, paired.DeviceToken);

    var result = await h.Ingest.IngestAsync(device!, [
        new RawDeviceEvent(EventSource.Notification, "FakeBank", "You have received Tk 500.00", now),
        new RawDeviceEvent(EventSource.Notification, "bKash", "", now),
        new RawDeviceEvent(EventSource.Notification, "bKash", "too old", now.AddDays(-8)),
        new RawDeviceEvent(EventSource.Notification, "bKash", "too new", now.AddMinutes(11)),
    ]);

    return result == new IngestRawEventsResult(0, 0, 4)
        && h.Db.RawEventsByDedupeHash.IsEmpty;
});

await CheckAsync("raw event ingest requires the paired device credential", async () =>
{
    var h = RawIngestHarness();
    var issued = await h.Devices.CreatePairingTokenAsync(merchantId);
    var paired = await h.Devices.PairAsync(new PairDeviceCommand(
        issued.Token,
        "payments-phone",
        null,
        null,
        null,
        null,
        null,
        null,
        null));

    var wrong = await h.Devices.AuthenticateAsync(paired.Device!.Id, "wrong");
    var right = await h.Devices.AuthenticateAsync(paired.Device.Id, paired.DeviceToken);

    return wrong is null && right is not null;
});

await CheckAsync("an order is accepted and starts ringing", async () =>
{
    var h = Harness();
    var r = await h.Create.CreateAsync(Cmd());

    return r.Outcome == CreateOrderOutcome.Created
        && r.Order!.Status == OrderStatus.Calling
        && r.Order.Msisdn == "8801711223344";
});

// The single most important property here. A shop's call can time out after we committed;
// a retry that made a second order would ring the customer twice.
await CheckAsync("the same reference twice makes one order", async () =>
{
    var h = Harness();
    var first = await h.Create.CreateAsync(Cmd());
    var again = await h.Create.CreateAsync(Cmd());

    return first.Outcome == CreateOrderOutcome.Created
        && again.Outcome == CreateOrderOutcome.AlreadyExists
        && again.Order!.Id == first.Order!.Id
        && h.Db.Orders.Count == 1;
});

await CheckAsync("a landline is refused at the door", async () =>
{
    var h = Harness();
    var r = await h.Create.CreateAsync(Cmd(msisdn: "029876543"));

    return r.Outcome == CreateOrderOutcome.Invalid && h.Db.Orders.IsEmpty;
});

await CheckAsync("an empty reference is refused", async () =>
    (await Harness().Create.CreateAsync(Cmd(reference: "  "))).Outcome == CreateOrderOutcome.Invalid);

await CheckAsync("a negative amount is refused", async () =>
    (await Harness().Create.CreateAsync(Cmd(amount: -5m))).Outcome == CreateOrderOutcome.Invalid);

await CheckAsync("an unknown merchant cannot place orders", async () =>
{
    var h = Harness();
    var r = await h.Create.CreateAsync(Cmd() with { MerchantId = Guid.CreateVersion7() });

    return r.Outcome == CreateOrderOutcome.NoMerchant;
});

await CheckAsync("a payments-only merchant is never rung", async () =>
{
    var h = Harness(calls: false, pay: true);
    var r = await h.Create.CreateAsync(Cmd());

    return r.Order!.Status == OrderStatus.AwaitingPayment;
});

await CheckAsync("the first event records how the order arrived", async () =>
{
    var h = Harness();
    await h.Create.CreateAsync(Cmd());

    var e = h.Db.Events.Single();

    return e.Type == "order.received" && e.Actor == Actor.Shop && e.To == OrderStatus.Calling;
});

// ---------------------------------------------------------------------------
// OrderConfirmation workflow
// ---------------------------------------------------------------------------
await CheckAsync("workflow starts a session when an order arrives", async () =>
{
    var h = WorkflowHarness();
    var r = await h.Workflow.CreateAsync(Cmd());
    var session = await h.Sessions.FindAsync(merchantId, r.Order!.Id, OrderWorkflowService.WorkflowName);

    return r.Outcome == CreateOrderOutcome.Created
        && session is not null
        && session.Step == OrderStatus.Calling.ToString()
        && session.History.SequenceEqual(["order.received"]);
});

await CheckAsync("workflow confirmation advances into payment when enabled", async () =>
{
    var h = WorkflowHarness(calls: true, pay: true);
    var r = await h.Workflow.CreateAsync(Cmd());

    var moved = await h.Workflow.DecideAsync(new WorkflowDecisionCommand
    {
        MerchantId = merchantId,
        OrderId = r.Order!.Id,
        To = OrderStatus.Confirmed,
        By = Actor.Machine,
        EventType = "order.confirmed",
    });

    var session = await h.Sessions.FindAsync(merchantId, r.Order.Id, OrderWorkflowService.WorkflowName);

    return moved.Outcome == TransitionOutcome.Moved
        && moved.Order!.Status == OrderStatus.AwaitingPayment
        && h.Db.Outbox.Count == 1
        && h.Db.Outbox[0].EventType == "order.confirmed"
        && session!.Step == OrderStatus.AwaitingPayment.ToString()
        && session.Complete;
});

await CheckAsync("workflow sends unclear call outcomes to human review", async () =>
{
    var h = WorkflowHarness(calls: true, pay: true);
    var r = await h.Workflow.CreateAsync(Cmd());

    var moved = await h.Workflow.DecideAsync(new WorkflowDecisionCommand
    {
        MerchantId = merchantId,
        OrderId = r.Order!.Id,
        To = OrderStatus.NeedsHuman,
        By = Actor.Machine,
        Reason = "no answer",
        EventType = "order.needs_human",
    });

    var session = await h.Sessions.FindAsync(merchantId, r.Order.Id, OrderWorkflowService.WorkflowName);

    return moved.Outcome == TransitionOutcome.Moved
        && moved.Order!.Status == OrderStatus.NeedsHuman
        && session!.Step == OrderStatus.NeedsHuman.ToString()
        && session.Complete;
});

await CheckAsync("workflow refuses illegal machine decisions", async () =>
{
    var h = WorkflowHarness(calls: true, pay: true);
    var r = await h.Workflow.CreateAsync(Cmd());
    await h.Workflow.DecideAsync(new WorkflowDecisionCommand
    {
        MerchantId = merchantId,
        OrderId = r.Order!.Id,
        To = OrderStatus.NeedsHuman,
        By = Actor.Machine,
        EventType = "order.needs_human",
    });

    var rejected = await h.Workflow.DecideAsync(new WorkflowDecisionCommand
    {
        MerchantId = merchantId,
        OrderId = r.Order.Id,
        To = OrderStatus.Rejected,
        By = Actor.Machine,
        EventType = "order.rejected",
    });

    return rejected.Outcome == TransitionOutcome.Refused
        && h.Db.Orders[r.Order.Id].Status == OrderStatus.NeedsHuman;
});

await CheckAsync("workflow leases allow only one runner at a time", async () =>
{
    var h = WorkflowHarness();
    var r = await h.Workflow.CreateAsync(Cmd());

    var first = await h.Sessions.TryLeaseNextAsync(
        OrderWorkflowService.WorkflowName, "runner-a", now, TimeSpan.FromMinutes(1));
    var second = await h.Sessions.TryLeaseNextAsync(
        OrderWorkflowService.WorkflowName, "runner-b", now, TimeSpan.FromMinutes(1));
    var expired = await h.Sessions.TryLeaseNextAsync(
        OrderWorkflowService.WorkflowName, "runner-b", now.AddMinutes(2), TimeSpan.FromMinutes(1));

    return first?.OrderId == r.Order!.Id
        && second is null
        && expired?.LeaseOwner == "runner-b";
});

await CheckAsync("workflow runner resumes an interrupted confirmation exactly once", async () =>
{
    var h = WorkflowHarness(calls: true, pay: true);
    var r = await h.Workflow.CreateAsync(Cmd());
    var order = h.Db.Orders[r.Order!.Id];
    order.Status = OrderStatus.Confirmed;
    order.ConfirmedAt = now;
    order.UpdatedAt = now;
    h.Db.Orders[order.Id] = order;

    var session = (await h.Sessions.FindAsync(merchantId, order.Id, OrderWorkflowService.WorkflowName))!;
    session.Step = OrderStatus.Confirmed.ToString();
    session.Complete = false;
    session.UpdatedAt = now.AddSeconds(-10);
    await h.Sessions.SaveAsync(session);

    var runner = new OrderWorkflowRunner(h.Sessions, h.Workflow, new FixedClock(now));
    var first = await runner.TickAsync("runner-a", TimeSpan.FromMinutes(1));
    var second = await runner.TickAsync("runner-a", TimeSpan.FromMinutes(1));
    var saved = await h.Sessions.FindAsync(merchantId, order.Id, OrderWorkflowService.WorkflowName);

    return first
        && !second
        && h.Db.Orders[order.Id].Status == OrderStatus.AwaitingPayment
        && saved!.Complete
        && saved.LeaseOwner is null
        && h.Db.Events.Count(e => e.To == OrderStatus.AwaitingPayment) == 1;
});

await CheckAsync("notify shop action queues outbox only once", async () =>
{
    var h = ActionHarness();
    var r = await h.Workflow.CreateAsync(Cmd());

    var first = await h.Actions.NotifyShopAsync(merchantId, r.Order!.Id);
    var again = await h.Actions.NotifyShopAsync(merchantId, r.Order.Id);

    return first.Start == WorkflowActionStart.Started
        && again.Start == WorkflowActionStart.AlreadyCompleted
        && h.Db.Outbox.Count == 1
        && h.Db.Outbox[0].EventType == "order.notification";
});

await CheckAsync("create invoice action creates invoice only once", async () =>
{
    var h = ActionHarness();
    var r = await h.Workflow.CreateAsync(Cmd());

    var first = await h.Actions.CreateInvoiceAsync(merchantId, r.Order!.Id);
    var again = await h.Actions.CreateInvoiceAsync(merchantId, r.Order.Id);

    return first.Start == WorkflowActionStart.Started
        && again.Start == WorkflowActionStart.AlreadyCompleted
        && h.Db.Invoices.Count == 1
        && h.Db.Orders[r.Order.Id].ChargedAmount == r.Order.Amount
        && h.Db.Events.Count(e => e.Type == "action.create_invoice") == 1;
});

await CheckAsync("create invoice action reserves unique payable amounts in the open window", async () =>
{
    var h = ActionHarness();
    var firstOrder = await h.Workflow.CreateAsync(Cmd(reference: "ORD-1", amount: 500m));
    var secondOrder = await h.Workflow.CreateAsync(Cmd(reference: "ORD-2", amount: 500m));

    await h.Actions.CreateInvoiceAsync(merchantId, firstOrder.Order!.Id);
    await h.Actions.CreateInvoiceAsync(merchantId, secondOrder.Order!.Id);

    var invoices = h.Db.Invoices.Values.OrderBy(i => i.OrderRef).ToList();

    return invoices.Count == 2
        && invoices[0].ChargedAmount == 500m
        && invoices[1].ChargedAmount == 501m
        && invoices.All(i => i.Mode == MatchingMode.UniqueAmount)
        && h.Db.Orders[secondOrder.Order.Id].ChargedAmount == 501m;
});

await CheckAsync("expired invoice amount is available for a new invoice", async () =>
{
    var h = ActionHarness();
    h.Db.Invoices[Guid.CreateVersion7()] = new Invoice
    {
        Id = Guid.CreateVersion7(),
        TenantId = merchantId,
        OrderRef = "OLD",
        Amount = 500m,
        ChargedAmount = 500m,
        Status = InvoiceStatus.AwaitingPayment,
        ExpiresAt = now.AddMinutes(-20),
        GraceUntil = now.AddMinutes(-1),
        CreatedAt = now.AddMinutes(-40),
        UpdatedAt = now.AddMinutes(-40),
    };

    var created = await h.Workflow.CreateAsync(Cmd(reference: "ORD-NEW", amount: 500m));
    await h.Actions.CreateInvoiceAsync(merchantId, created.Order!.Id);

    var invoice = h.Db.Invoices.Values.Single(i => i.OrderRef == "ORD-NEW");

    return invoice.ChargedAmount == 500m
        && invoice.Mode == MatchingMode.UniqueAmount;
});

await CheckAsync("invoice falls back to transaction id mode when unique amount slots are full", async () =>
{
    var h = ActionHarness();

    foreach (var salt in Enumerable.Range(0, 2))
    {
        h.Db.Invoices[Guid.CreateVersion7()] = new Invoice
        {
            Id = Guid.CreateVersion7(),
            TenantId = merchantId,
            OrderRef = $"TAKEN-{salt}",
            Amount = 50m,
            ChargedAmount = 50m + salt,
            Status = InvoiceStatus.AwaitingPayment,
            ExpiresAt = now.AddMinutes(10),
            GraceUntil = now.AddMinutes(25),
            CreatedAt = now,
            UpdatedAt = now,
            Mode = MatchingMode.UniqueAmount,
        };
    }

    var created = await h.Workflow.CreateAsync(Cmd(reference: "ORD-FALLBACK", amount: 50m));
    await h.Actions.CreateInvoiceAsync(merchantId, created.Order!.Id);

    var invoice = h.Db.Invoices.Values.Single(i => i.OrderRef == "ORD-FALLBACK");

    return invoice.ChargedAmount == 50m
        && invoice.Mode == MatchingMode.TrxId
        && h.Db.Orders[created.Order.Id].ChargedAmount == 50m;
});

await CheckAsync("payment pipeline matches a bKash credit to the unique amount invoice", async () =>
{
    var h = PaymentPipelineHarness();
    var created = await h.Workflow.CreateAsync(Cmd(reference: "ORD-PAY", amount: 500m));
    await h.Actions.CreateInvoiceAsync(merchantId, created.Order!.Id);
    var device = new AppDevice
    {
        Id = Guid.CreateVersion7(),
        MerchantId = merchantId,
        DeviceTokenHash = "hash",
        Fingerprint = "payments-phone",
        CreatedAt = now,
        UpdatedAt = now,
    };

    await h.Ingest.IngestAsync(device, [
        new RawDeviceEvent(
            EventSource.Notification,
            "bKash",
            "You have received Tk 500.00 from 01910126335. Fee Tk 0.00. Balance Tk 1,881.86. TrxID DI28000001 at 28/09/2026 16:00",
            now),
    ]);

    var result = await h.Pipeline.RunOnceAsync();
    var invoice = h.Db.Invoices.Values.Single(i => i.OrderRef == "ORD-PAY");
    var order = h.Db.Orders[created.Order.Id];

    return result.Processed == 1
        && result.Settled == 1
        && result.Items[0].Match == MatchOutcome.Matched
        && result.Items[0].Strategy == MatchStrategy.UniqueAmount
        && invoice.Status == InvoiceStatus.Paid
        && invoice.PaidAt == now
        && order.Status == OrderStatus.Paid
        && order.PaidAmount == 500m
        && order.TrxId == "DI28000001"
        && h.Db.Events.Count(e => e.Type == "order.paid") == 1
        && h.Db.Outbox.Count(e => e.EventType == "order.paid") == 1
        && h.Db.PaymentMatches.Count == 1
        && h.Db.RawEventsByDedupeHash.Values.Single().State == RawEventState.Parsed;
});

await CheckAsync("payment pipeline does not settle the same provider trx id twice", async () =>
{
    var h = PaymentPipelineHarness();
    var first = await h.Workflow.CreateAsync(Cmd(reference: "ORD-A", amount: 500m));
    var second = await h.Workflow.CreateAsync(Cmd(reference: "ORD-B", amount: 501m));
    await h.Actions.CreateInvoiceAsync(merchantId, first.Order!.Id);
    await h.Actions.CreateInvoiceAsync(merchantId, second.Order!.Id);
    var device = new AppDevice
    {
        Id = Guid.CreateVersion7(),
        MerchantId = merchantId,
        DeviceTokenHash = "hash",
        Fingerprint = "payments-phone",
        CreatedAt = now,
        UpdatedAt = now,
    };

    await h.Ingest.IngestAsync(device, [
        new RawDeviceEvent(
            EventSource.Notification,
            "bKash",
            "You have received Tk 500.00 from 01910126335. Fee Tk 0.00. Balance Tk 1,881.86. TrxID DI28000002 at 28/09/2026 16:00",
            now),
    ]);
    await h.Pipeline.RunOnceAsync();

    h.Db.RawEventsByDedupeHash[$"manual-{Guid.CreateVersion7():N}"] = new RawEvent
    {
        Id = Guid.CreateVersion7(),
        TenantId = merchantId,
        DeviceId = device.Id,
        Source = EventSource.Sms,
        SenderId = "bKash",
        Body = "You have received Tk 501.00 from 01910126335. Fee Tk 0.00. Balance Tk 2,382.86. TrxID DI28000002 at 28/09/2026 16:00",
        DeviceReceivedAt = now.AddSeconds(1),
        ServerReceivedAt = now.AddSeconds(1),
        DedupeHash = $"manual-{Guid.CreateVersion7():N}",
        State = RawEventState.Received,
        CreatedAt = now,
        UpdatedAt = now,
    };

    var replay = await h.Pipeline.RunOnceAsync();
    var secondInvoice = h.Db.Invoices.Values.Single(i => i.OrderRef == "ORD-B");

    return replay.Items.Single().Match == MatchOutcome.AlreadyProcessed
        && secondInvoice.Status == InvoiceStatus.AwaitingPayment
        && h.Db.PaymentMatches.Count == 1
        && h.Db.Outbox.Count(e => e.EventType == "order.paid") == 1;
});

await CheckAsync("payment pipeline leaves unmatched payments for review", async () =>
{
    var h = PaymentPipelineHarness();
    var created = await h.Workflow.CreateAsync(Cmd(reference: "ORD-PAY", amount: 500m));
    await h.Actions.CreateInvoiceAsync(merchantId, created.Order!.Id);
    var device = new AppDevice
    {
        Id = Guid.CreateVersion7(),
        MerchantId = merchantId,
        DeviceTokenHash = "hash",
        Fingerprint = "payments-phone",
        CreatedAt = now,
        UpdatedAt = now,
    };

    await h.Ingest.IngestAsync(device, [
        new RawDeviceEvent(
            EventSource.Notification,
            "bKash",
            "You have received Tk 700.00 from 01910126335. Fee Tk 0.00. Balance Tk 1,881.86. TrxID DI28000003 at 28/09/2026 16:00",
            now),
    ]);

    var result = await h.Pipeline.RunOnceAsync();
    var invoice = h.Db.Invoices.Values.Single(i => i.OrderRef == "ORD-PAY");

    return result.NeedsAttention == 1
        && result.Items.Single().Match == MatchOutcome.Unmatched
        && invoice.Status == InvoiceStatus.AwaitingPayment
        && h.Db.PaymentMatches.IsEmpty;
});

await CheckAsync("unmatched payment appears in payment review", async () =>
{
    var h = PaymentPipelineHarness();
    var created = await h.Workflow.CreateAsync(Cmd(reference: "ORD-PAY", amount: 500m));
    await h.Actions.CreateInvoiceAsync(merchantId, created.Order!.Id);
    var device = new AppDevice
    {
        Id = Guid.CreateVersion7(),
        MerchantId = merchantId,
        DeviceTokenHash = "hash",
        Fingerprint = "payments-phone",
        CreatedAt = now,
        UpdatedAt = now,
    };

    await h.Ingest.IngestAsync(device, [
        new RawDeviceEvent(
            EventSource.Notification,
            "bKash",
            "You have received Tk 700.00 from 01910126335. Fee Tk 0.00. Balance Tk 1,881.86. TrxID DI28000004 at 28/09/2026 16:00",
            now),
    ]);
    await h.Pipeline.RunOnceAsync();

    var review = await h.PaymentReviews.ListAsync(merchantId);

    return review.Count == 1
        && review[0].Reason == "No open invoice expects this amount."
        && review[0].Body.Contains("DI28000004", StringComparison.Ordinal);
});

await CheckAsync("manual payment review match settles order and writes audit", async () =>
{
    var h = PaymentPipelineHarness();
    var created = await h.Workflow.CreateAsync(Cmd(reference: "ORD-PAY", amount: 500m));
    await h.Actions.CreateInvoiceAsync(merchantId, created.Order!.Id);
    var device = new AppDevice
    {
        Id = Guid.CreateVersion7(),
        MerchantId = merchantId,
        DeviceTokenHash = "hash",
        Fingerprint = "payments-phone",
        CreatedAt = now,
        UpdatedAt = now,
    };

    await h.Ingest.IngestAsync(device, [
        new RawDeviceEvent(
            EventSource.Notification,
            "bKash",
            "You have received Tk 700.00 from 01910126335. Fee Tk 0.00. Balance Tk 1,881.86. TrxID DI28000005 at 28/09/2026 16:00",
            now),
    ]);
    await h.Pipeline.RunOnceAsync();
    var rawEventId = h.Db.RawEventsByDedupeHash.Values.Single().Id;

    var matched = await h.PaymentReviews.ManualMatchAsync(new ManualPaymentMatchCommand(
        merchantId,
        rawEventId,
        "ORD-PAY",
        "rafi",
        "customer paid a different amount"));
    var invoice = h.Db.Invoices.Values.Single(i => i.OrderRef == "ORD-PAY");
    var order = h.Db.Orders[created.Order!.Id];

    return matched.Outcome == ManualPaymentMatchOutcome.Matched
        && invoice.Status == InvoiceStatus.Paid
        && order.Status == OrderStatus.Paid
        && order.PaidAmount == 700m
        && order.TrxId == "DI28000005"
        && h.Db.PaymentMatches.Values.Single().Strategy == MatchStrategy.Manual
        && h.Db.Events.Any(e => e.Type == "payment.manual_match" && e.ActorName == "rafi")
        && h.Db.Outbox.Count(e => e.EventType == "order.paid") == 1
        && h.Db.RawEventsByDedupeHash.Values.Single().FailureReason is null;
});

await CheckAsync("manual payment review match refuses an already matched transaction", async () =>
{
    var h = PaymentPipelineHarness();
    var first = await h.Workflow.CreateAsync(Cmd(reference: "ORD-A", amount: 500m));
    var second = await h.Workflow.CreateAsync(Cmd(reference: "ORD-B", amount: 600m));
    await h.Actions.CreateInvoiceAsync(merchantId, first.Order!.Id);
    await h.Actions.CreateInvoiceAsync(merchantId, second.Order!.Id);
    var device = new AppDevice
    {
        Id = Guid.CreateVersion7(),
        MerchantId = merchantId,
        DeviceTokenHash = "hash",
        Fingerprint = "payments-phone",
        CreatedAt = now,
        UpdatedAt = now,
    };

    await h.Ingest.IngestAsync(device, [
        new RawDeviceEvent(
            EventSource.Notification,
            "bKash",
            "You have received Tk 500.00 from 01910126335. Fee Tk 0.00. Balance Tk 1,881.86. TrxID DI28000006 at 28/09/2026 16:00",
            now),
    ]);
    await h.Pipeline.RunOnceAsync();

    h.Db.RawEventsByDedupeHash[$"manual-{Guid.CreateVersion7():N}"] = new RawEvent
    {
        Id = Guid.CreateVersion7(),
        TenantId = merchantId,
        DeviceId = device.Id,
        Source = EventSource.Sms,
        SenderId = "bKash",
        Body = "You have received Tk 600.00 from 01910126335. Fee Tk 0.00. Balance Tk 2,481.86. TrxID DI28000006 at 28/09/2026 16:00",
        DeviceReceivedAt = now.AddSeconds(1),
        ServerReceivedAt = now.AddSeconds(1),
        DedupeHash = $"manual-{Guid.CreateVersion7():N}",
        State = RawEventState.Parsed,
        FailureReason = "No open invoice expects this amount.",
        CreatedAt = now,
        UpdatedAt = now,
    };
    var rawEventId = h.Db.RawEventsByDedupeHash.Values.Single(e => e.FailureReason is not null).Id;

    var refused = await h.PaymentReviews.ManualMatchAsync(new ManualPaymentMatchCommand(
        merchantId,
        rawEventId,
        "ORD-B",
        "rafi",
        null));

    return refused.Outcome == ManualPaymentMatchOutcome.AlreadyMatched
        && h.Db.PaymentMatches.Count == 1
        && h.Db.Invoices.Values.Single(i => i.OrderRef == "ORD-B").Status == InvoiceStatus.AwaitingPayment;
});

await CheckAsync("payment e2e simulator pays an order from a paired device event", async () =>
{
    var h = PaymentE2eHarness();
    var issued = await h.Devices.CreatePairingTokenAsync(merchantId);
    var paired = await h.Devices.PairAsync(new PairDeviceCommand(
        issued.Token,
        "sim-phone-1",
        "Counter phone",
        "Galaxy A",
        "1.0.0",
        null,
        DevicePermissionState.Healthy,
        83,
        "wifi"));
    var device = await h.Devices.AuthenticateAsync(paired.Device!.Id, paired.DeviceToken);
    var created = await h.Workflow.CreateAsync(Cmd(reference: "SIM-PAID", amount: 500m));
    var action = await h.Actions.CreateInvoiceAsync(merchantId, created.Order!.Id);
    var invoiceBefore = h.Db.Invoices.Values.Single(i => i.OrderRef == "SIM-PAID");
    var body =
        $"You have received Tk {invoiceBefore.ChargedAmount:0.00} from 01910126335. Fee Tk 0.00. Balance Tk 1,881.86. TrxID DI28009901 at 28/09/2026 16:00";

    var ingest = await h.Ingest.IngestAsync(device!, [
        new RawDeviceEvent(EventSource.Notification, "bKash", body, now),
        new RawDeviceEvent(EventSource.Notification, "bKash", body, now),
    ]);
    var matched = await h.Pipeline.RunOnceAsync();
    var replay = await h.Pipeline.RunOnceAsync();
    var paidInvoice = h.Db.Invoices.Values.Single(i => i.OrderRef == "SIM-PAID");
    var paidOrder = h.Db.Orders[created.Order.Id];

    h.Clock.Set(now.Add(DeviceOfflineAlertService.OfflineAfter).AddSeconds(1));
    var offline = await h.OfflineAlerts.RunOnceAsync();
    var recovered = await h.Devices.HeartbeatAsync(
        paired.Device.Id,
        paired.DeviceToken,
        new DeviceHeartbeatCommand(DevicePermissionState.Healthy, 79, "4g", "1.0.1", null));

    return paired.Outcome == PairDeviceOutcome.Paired
        && device is not null
        && action.Start == WorkflowActionStart.Started
        && ingest.Accepted == 1
        && ingest.Duplicates == 1
        && matched.Processed == 1
        && matched.Settled == 1
        && replay.Processed == 0
        && paidInvoice.Status == InvoiceStatus.Paid
        && paidOrder.Status == OrderStatus.Paid
        && paidOrder.PaidAmount == invoiceBefore.ChargedAmount
        && paidOrder.TrxId == "DI28009901"
        && h.Db.PaymentMatches.Count == 1
        && h.Db.ParsedTransactions.Values.Single().RawEventId ==
            h.Db.RawEventsByDedupeHash.Values.Single().Id
        && h.Db.Outbox.Count(e => e.EventType == "order.paid") == 1
        && offline.Alerted == 1
        && h.Db.Outbox.Count(e => e.EventType == "device.offline") == 1
        && recovered.Outcome == DeviceUpdateOutcome.Updated
        && h.Db.AppDevices[paired.Device.Id].OfflineAlertedAt is null;
});

await CheckAsync("payment e2e simulator sends wrong amount to review and manual match", async () =>
{
    var h = PaymentE2eHarness();
    var issued = await h.Devices.CreatePairingTokenAsync(merchantId);
    var paired = await h.Devices.PairAsync(new PairDeviceCommand(
        issued.Token,
        "sim-phone-2",
        null,
        null,
        "1.0.0",
        null,
        DevicePermissionState.Healthy,
        90,
        "wifi"));
    var device = await h.Devices.AuthenticateAsync(paired.Device!.Id, paired.DeviceToken);
    var created = await h.Workflow.CreateAsync(Cmd(reference: "SIM-REVIEW", amount: 500m));
    await h.Actions.CreateInvoiceAsync(merchantId, created.Order!.Id);

    var ingest = await h.Ingest.IngestAsync(device!, [
        new RawDeviceEvent(
            EventSource.Notification,
            "bKash",
            "You have received Tk 700.00 from 01910126335. Fee Tk 0.00. Balance Tk 1,881.86. TrxID DI28009902 at 28/09/2026 16:00",
            now),
    ]);
    var pipeline = await h.Pipeline.RunOnceAsync();
    var review = await h.PaymentReviews.ListAsync(merchantId);
    var manual = await h.PaymentReviews.ManualMatchAsync(new ManualPaymentMatchCommand(
        merchantId,
        review.Single().RawEventId,
        "SIM-REVIEW",
        "sim-reviewer",
        "customer paid a different amount"));
    var invoice = h.Db.Invoices.Values.Single(i => i.OrderRef == "SIM-REVIEW");
    var order = h.Db.Orders[created.Order.Id];

    return ingest.Accepted == 1
        && pipeline.NeedsAttention == 1
        && pipeline.Items.Single().Match == MatchOutcome.Unmatched
        && review.Count == 1
        && review[0].Reason == "No open invoice expects this amount."
        && manual.Outcome == ManualPaymentMatchOutcome.Matched
        && invoice.Status == InvoiceStatus.Paid
        && order.Status == OrderStatus.Paid
        && order.PaidAmount == 700m
        && order.TrxId == "DI28009902"
        && h.Db.PaymentMatches.Values.Single().Strategy == MatchStrategy.Manual
        && h.Db.Events.Any(e => e.Type == "payment.manual_match"
            && e.ActorName == "sim-reviewer")
        && h.Db.Outbox.Count(e => e.EventType == "order.paid") == 1
        && h.Db.RawEventsByDedupeHash.Values.Single().FailureReason is null;
});

await CheckAsync("book courier action books courier only once", async () =>
{
    var h = ActionHarness(calls: false, pay: true, courier: true);
    var r = await h.Workflow.CreateAsync(Cmd());
    await h.Workflow.DecideAsync(new WorkflowDecisionCommand
    {
        MerchantId = merchantId,
        OrderId = r.Order!.Id,
        To = OrderStatus.Paid,
        By = Actor.Machine,
        EventType = "order.paid",
    });

    var first = await h.Actions.BookCourierAsync(merchantId, r.Order.Id);
    var again = await h.Actions.BookCourierAsync(merchantId, r.Order.Id);

    return first.Start == WorkflowActionStart.Started
        && again.Start == WorkflowActionStart.AlreadyCompleted
        && h.Db.Orders[r.Order.Id].Status == OrderStatus.Booked
        && h.Db.Orders[r.Order.Id].TrackingCode == $"PENDING-{r.Order.Reference}"
        && h.Db.Events.Count(e => e.Type == "order.booked") == 1;
});

await CheckAsync("AI high-confidence confirm may execute through the workflow", async () =>
{
    var h = AiGateHarness(calls: true, pay: false);
    var r = await h.Workflow.CreateAsync(Cmd());

    var gate = await h.Gate.ApplyAsync(new AiProposal
    {
        MerchantId = merchantId,
        OrderId = r.Order!.Id,
        Outcome = AiProposedOutcome.Confirm,
        Confidence = 0.96,
    });

    return gate.Outcome == AiGateOutcome.Executed
        && gate.Order!.Status == OrderStatus.Confirmed
        && h.Db.Events.Any(e => e.Type == "order.confirmed");
});

await CheckAsync("AI high-confidence reject still goes to human review", async () =>
{
    var h = AiGateHarness(calls: true, pay: false);
    var r = await h.Workflow.CreateAsync(Cmd());

    var gate = await h.Gate.ApplyAsync(new AiProposal
    {
        MerchantId = merchantId,
        OrderId = r.Order!.Id,
        Outcome = AiProposedOutcome.Reject,
        Confidence = 0.99,
    });

    return gate.Outcome == AiGateOutcome.SentToReview
        && gate.Order!.Status == OrderStatus.NeedsHuman
        && gate.Reason == "ai.reject_requires_human";
});

await CheckAsync("AI low-confidence proposals go to human review", async () =>
{
    var h = AiGateHarness(calls: true, pay: false);
    var r = await h.Workflow.CreateAsync(Cmd());

    var gate = await h.Gate.ApplyAsync(new AiProposal
    {
        MerchantId = merchantId,
        OrderId = r.Order!.Id,
        Outcome = AiProposedOutcome.Confirm,
        Confidence = 0.50,
    });

    return gate.Outcome == AiGateOutcome.SentToReview
        && gate.Order!.Status == OrderStatus.NeedsHuman
        && gate.Reason == "ai.low_confidence";
});

await CheckAsync("AI proposals against lifecycle rules go to human review", async () =>
{
    var h = AiGateHarness(calls: true, pay: false);
    var r = await h.Workflow.CreateAsync(Cmd());
    await h.Workflow.DecideAsync(new WorkflowDecisionCommand
    {
        MerchantId = merchantId,
        OrderId = r.Order!.Id,
        To = OrderStatus.NeedsHuman,
        By = Actor.Machine,
        EventType = "order.needs_human",
    });

    var gate = await h.Gate.ApplyAsync(new AiProposal
    {
        MerchantId = merchantId,
        OrderId = r.Order.Id,
        Outcome = AiProposedOutcome.Confirm,
        Confidence = 0.95,
    });

    return gate.Outcome == AiGateOutcome.SentToReview
        && gate.Order!.Status == OrderStatus.NeedsHuman;
});

await CheckAsync("voice AI confirm executes only through the phase 10 gate", async () =>
{
    var h = VoiceAiHarness("""{"outcome":"confirm","confidence":0.96}""", calls: true, pay: false);
    var r = await h.Workflow.CreateAsync(Cmd());

    var decision = await h.Decisions.DecideAsync(VoiceDecision(r.Order!.Id, "yes, please confirm the order"));

    return decision.Outcome == AiGateOutcome.Executed
        && decision.ProposedOutcome == AiProposedOutcome.Confirm
        && decision.Order!.Status == OrderStatus.Confirmed
        && h.Db.Events.Any(e => e.Type == "order.confirmed");
});

await CheckAsync("voice AI reject is routed to human review by the gate", async () =>
{
    var h = VoiceAiHarness("""{"outcome":"reject","confidence":0.99}""", calls: true, pay: false);
    var r = await h.Workflow.CreateAsync(Cmd());

    var decision = await h.Decisions.DecideAsync(VoiceDecision(r.Order!.Id, "no, I do not want this order"));

    return decision.Outcome == AiGateOutcome.SentToReview
        && decision.ProposedOutcome == AiProposedOutcome.Reject
        && decision.Order!.Status == OrderStatus.NeedsHuman
        && decision.Reason == "ai.reject_requires_human"
        && h.Db.Events.Any(e => e.Type == "voice.handoff_context"
            && e.OrderId == r.Order.Id
            && e.Detail == "no, I do not want this order");
});

await CheckAsync("voice AI low-confidence confirm goes to human review", async () =>
{
    var h = VoiceAiHarness("""{"outcome":"confirm","confidence":0.51}""", calls: true, pay: false);
    var r = await h.Workflow.CreateAsync(Cmd());

    var decision = await h.Decisions.DecideAsync(VoiceDecision(r.Order!.Id, "I think it is okay"));

    return decision.Outcome == AiGateOutcome.SentToReview
        && decision.Order!.Status == OrderStatus.NeedsHuman
        && decision.Reason == "ai.low_confidence";
});

await CheckAsync("voice AI malformed output falls back to human review", async () =>
{
    var h = VoiceAiHarness("probably confirmed", calls: true, pay: false);
    var r = await h.Workflow.CreateAsync(Cmd());

    var decision = await h.Decisions.DecideAsync(VoiceDecision(r.Order!.Id, "okay"));

    return decision.Outcome == AiGateOutcome.SentToReview
        && decision.ProposedOutcome == AiProposedOutcome.NeedsHuman
        && decision.Order!.Status == OrderStatus.NeedsHuman;
});

await CheckAsync("voice AI sends final transcript text to the model", async () =>
{
    var h = VoiceAiHarness("""{"outcome":"needs_human","confidence":1}""", calls: true, pay: false);
    var r = await h.Workflow.CreateAsync(Cmd());

    await h.Decisions.DecideAsync(new VoiceAiDecisionCommand
    {
        MerchantId = merchantId,
        OrderId = r.Order!.Id,
        CallSessionId = Guid.CreateVersion7(),
        Transcript =
        [
            new Transcript("partial words", false, 0.4, now),
            new Transcript("final answer", true, 0.9, now.AddSeconds(1)),
        ],
    });

    return h.Model.LastUserText == "final answer";
});

await CheckAsync("handoff context is written to the order timeline", async () =>
{
    var h = HandoffHarness();
    var r = await h.Workflow.CreateAsync(Cmd());
    await h.Workflow.DecideAsync(new WorkflowDecisionCommand
    {
        MerchantId = merchantId,
        OrderId = r.Order!.Id,
        To = OrderStatus.NeedsHuman,
        By = Actor.Machine,
        Reason = "customer asked for a person",
        EventType = "order.needs_human",
    });

    var callSession = Guid.Parse("01929999-0000-7000-8000-000000000028");
    var result = await h.Handoff.RecordAsync(new HandoffContextCommand
    {
        MerchantId = merchantId,
        OrderId = r.Order.Id,
        CallSessionId = callSession,
        TranscriptText = "Agent: Do you confirm?\nCustomer: I need to talk to someone.",
        HandoffReason = "customer requested human help",
    });

    var handoff = h.Db.Events.Single(e => e.Type == "voice.handoff_context");
    return result.Outcome == HandoffContextOutcome.Recorded
        && result.Context is { UsedFallback: true, Provider: "deterministic-fallback" }
        && handoff.OrderId == r.Order.Id
        && handoff.From == OrderStatus.NeedsHuman
        && handoff.To == OrderStatus.NeedsHuman
        && handoff.Detail == "I need to talk to someone."
        && handoff.PayloadJson is not null
        && handoff.PayloadJson.Contains(callSession.ToString(), StringComparison.Ordinal)
        && handoff.PayloadJson.Contains("suggestedOpening", StringComparison.Ordinal);
});

await CheckAsync("handoff summary exists before a human claims the review", async () =>
{
    var h = HandoffHarness();
    var r = await h.Workflow.CreateAsync(Cmd());
    await h.Workflow.DecideAsync(new WorkflowDecisionCommand
    {
        MerchantId = merchantId,
        OrderId = r.Order!.Id,
        To = OrderStatus.NeedsHuman,
        By = Actor.Machine,
        Reason = "ai.low_confidence",
        EventType = "order.needs_human",
    });

    await h.Handoff.RecordAsync(new HandoffContextCommand
    {
        MerchantId = merchantId,
        OrderId = r.Order.Id,
        CallSessionId = Guid.CreateVersion7(),
        TranscriptText = "Customer: Maybe later.",
        HandoffReason = "low confidence model decision",
    });

    await h.Reviews.ClaimAsync(new ReviewClaimCommand
    {
        MerchantId = merchantId,
        OrderId = r.Order.Id,
        Reviewer = "rafi",
    });

    var handoff = h.Db.Events.Single(e => e.Type == "voice.handoff_context");
    var claim = h.Db.Events.Single(e => e.Type == "review.claimed");
    return handoff.At <= claim.At;
});

await CheckAsync("handoff context handles an empty transcript", async () =>
{
    var h = HandoffHarness();
    var r = await h.Workflow.CreateAsync(Cmd());

    var result = await h.Handoff.RecordAsync(new HandoffContextCommand
    {
        MerchantId = merchantId,
        OrderId = r.Order!.Id,
        CallSessionId = Guid.CreateVersion7(),
        TranscriptText = "",
        HandoffReason = "caller pressed 9",
    });

    var handoff = h.Db.Events.Single(e => e.Type == "voice.handoff_context");
    return result.Outcome == HandoffContextOutcome.Recorded
        && result.Context!.Summary == "caller pressed 9"
        && result.Context.SuggestedOpening.Length > 0
        && handoff.Detail == "caller pressed 9";
});

await CheckAsync("handoff context for a missing order is not recorded", async () =>
{
    var h = HandoffHarness();

    var result = await h.Handoff.RecordAsync(new HandoffContextCommand
    {
        MerchantId = merchantId,
        OrderId = Guid.CreateVersion7(),
        CallSessionId = Guid.CreateVersion7(),
        TranscriptText = "Customer: hello",
        HandoffReason = "not found",
    });

    return result.Outcome == HandoffContextOutcome.NotFound
        && h.Db.Events.All(e => e.Type != "voice.handoff_context");
});

Check("recording playback URLs expire and cannot be tampered", () =>
{
    var signer = new RecordingPlaybackUrlSigner("recording-test-secret");
    var key = "recordings/m/order/session/call.wav";
    var expiresAt = now.AddMinutes(15);
    var url = signer.Sign(key, expiresAt);
    var query = Query(url);
    var expires = long.Parse(query["expires"], System.Globalization.CultureInfo.InvariantCulture);
    var sig = query["sig"];

    return signer.Verify(key, expires, sig, now.AddMinutes(14))
        && !signer.Verify(key + ".other", expires, sig, now.AddMinutes(14))
        && !signer.Verify(key, expires, sig, now.AddMinutes(16));
});

await CheckAsync("call recording is archived and written to the order timeline", async () =>
{
    var h = RecordingHarness();
    var r = await h.Workflow.CreateAsync(Cmd());
    var session = Guid.Parse("01929999-0000-7000-8000-000000000029");
    var recordingName = CallRecordingService.RecordingName(session);

    var result = await h.Recordings.StoreFinishedAsync(new RecordingFinishedCommand
    {
        MerchantId = merchantId,
        OrderId = r.Order!.Id,
        CallSessionId = session,
        RecordingName = recordingName,
    });

    var eventRow = h.Db.Events.Single(e => e.Type == "voice.recording_stored");
    return result.Outcome == RecordingStoreOutcome.Recorded
        && result.ObjectStorageKey is not null
        && result.ObjectStorageKey.Contains(session.ToString(), StringComparison.Ordinal)
        && result.PlaybackUrl is not null
        && result.PlaybackUrl.Contains("expires=", StringComparison.Ordinal)
        && h.Archive.LastRecordingName == recordingName
        && h.Archive.LastObjectStorageKey == result.ObjectStorageKey
        && eventRow.OrderId == r.Order.Id
        && eventRow.PayloadJson is not null
        && eventRow.PayloadJson.Contains("playbackUrl", StringComparison.Ordinal)
        && eventRow.PayloadJson.Contains("objectStorageKey", StringComparison.Ordinal);
});

await CheckAsync("recording playback requires the owning merchant", async () =>
{
    var h = RecordingHarness();
    var r = await h.Workflow.CreateAsync(Cmd());
    var session = Guid.CreateVersion7();
    var recordingName = CallRecordingService.RecordingName(session);
    var stored = await h.Recordings.StoreFinishedAsync(new RecordingFinishedCommand
    {
        MerchantId = merchantId,
        OrderId = r.Order!.Id,
        CallSessionId = session,
        RecordingName = recordingName,
    });

    var query = Query(stored.PlaybackUrl!);
    var expires = long.Parse(query["expires"], System.Globalization.CultureInfo.InvariantCulture);
    var key = query["key"];
    var signature = query["sig"];

    var mine = await h.Recordings.AuthorizePlaybackAsync(new RecordingPlaybackRequest(
        merchantId, key, expires, signature));
    var other = await h.Recordings.AuthorizePlaybackAsync(new RecordingPlaybackRequest(
        Guid.CreateVersion7(), key, expires, signature));

    return mine.Outcome == RecordingPlaybackOutcome.Authorized
        && mine.ObjectStorageKey == key
        && other.Outcome == RecordingPlaybackOutcome.Forbidden;
});

await CheckAsync("recording retention deletes audio from storage", async () =>
{
    var h = RecordingHarness();
    var r = await h.Workflow.CreateAsync(Cmd());
    var session = Guid.CreateVersion7();
    var stored = await h.Recordings.StoreFinishedAsync(new RecordingFinishedCommand
    {
        MerchantId = merchantId,
        OrderId = r.Order!.Id,
        CallSessionId = session,
        RecordingName = CallRecordingService.RecordingName(session),
    });
    var key = stored.ObjectStorageKey!;

    var before = await h.Archive.ExistsAsync(key);
    var sweep = await h.Recordings.SweepExpiredAsync([key], now.AddSeconds(-1));
    var after = await h.Archive.ExistsAsync(key);
    var query = Query(stored.PlaybackUrl!);
    var expires = long.Parse(query["expires"], System.Globalization.CultureInfo.InvariantCulture);
    var playback = await h.Recordings.AuthorizePlaybackAsync(new RecordingPlaybackRequest(
        merchantId, key, expires, query["sig"]));

    return before
        && sweep == new RecordingRetentionResult(1, 1)
        && !after
        && playback.Outcome == RecordingPlaybackOutcome.Gone;
});

await CheckAsync("recording storage for a missing order is ignored", async () =>
{
    var h = RecordingHarness();

    var result = await h.Recordings.StoreFinishedAsync(new RecordingFinishedCommand
    {
        MerchantId = merchantId,
        OrderId = Guid.CreateVersion7(),
        CallSessionId = Guid.CreateVersion7(),
        RecordingName = "missing-recording",
    });

    return result.Outcome == RecordingStoreOutcome.NotFound
        && h.Archive.LastRecordingName is null
        && h.Db.Events.All(e => e.Type != "voice.recording_stored");
});

Check("recording correlation survives channel release", () =>
{
    var calls = new MoynaPay.Application.Voice.Ari.CallCorrelator();
    var session = Guid.CreateVersion7();
    var name = CallRecordingService.RecordingName(session);
    calls.Register(session, merchantId, Guid.CreateVersion7(), "shop", now);
    calls.ExpectRecording(session, name);
    calls.Release(session);

    return calls.TryReleaseRecording(name, out var call)
        && call.CallSessionId == session
        && !calls.TryReleaseRecording(name, out _);
});

// ---------------------------------------------------------------------------
// Review queue
// ---------------------------------------------------------------------------
async Task<(MemoryDatabase Db, ReviewQueueService Reviews, Order Order)> NeedsReview(
    DateTimeOffset? at = null)
{
    var h = ReviewHarness(at: at);
    var r = await h.Workflow.CreateAsync(Cmd());
    await h.Workflow.DecideAsync(new WorkflowDecisionCommand
    {
        MerchantId = merchantId,
        OrderId = r.Order!.Id,
        To = OrderStatus.NeedsHuman,
        By = Actor.Machine,
        Reason = "unclear",
        EventType = "order.needs_human",
    });

    return (h.Db, h.Reviews, h.Db.Orders[r.Order.Id]);
}

await CheckAsync("two reviewers cannot claim the same review item", async () =>
{
    var h = await NeedsReview();

    var first = await h.Reviews.ClaimAsync(new ReviewClaimCommand
    {
        MerchantId = merchantId,
        OrderId = h.Order.Id,
        Reviewer = "rafi",
        ClaimFor = TimeSpan.FromMinutes(5),
    });
    var second = await h.Reviews.ClaimAsync(new ReviewClaimCommand
    {
        MerchantId = merchantId,
        OrderId = h.Order.Id,
        Reviewer = "sadia",
        ClaimFor = TimeSpan.FromMinutes(5),
    });

    return first.Outcome == ReviewClaimOutcome.Claimed
        && second.Outcome == ReviewClaimOutcome.AlreadyClaimed
        && h.Db.Orders[h.Order.Id].ClaimedBy == "rafi"
        && h.Db.Events.Any(e => e.Type == "review.claimed" && e.ActorName == "rafi");
});

await CheckAsync("expired review claims return to the queue", async () =>
{
    var h = await NeedsReview();
    await h.Reviews.ClaimAsync(new ReviewClaimCommand
    {
        MerchantId = merchantId,
        OrderId = h.Order.Id,
        Reviewer = "rafi",
        ClaimFor = TimeSpan.FromSeconds(1),
    });

    var afterExpiry = ReviewHarness(at: now.AddSeconds(2));
    afterExpiry.Db.Orders[h.Order.Id] = h.Db.Orders[h.Order.Id];
    afterExpiry.Db.Events.AddRange(h.Db.Events);
    var available = await afterExpiry.Reviews.ListAvailableAsync(merchantId);
    var claimed = await afterExpiry.Reviews.ClaimAsync(new ReviewClaimCommand
    {
        MerchantId = merchantId,
        OrderId = h.Order.Id,
        Reviewer = "sadia",
        ClaimFor = TimeSpan.FromMinutes(5),
    });

    return available.Any(o => o.Id == h.Order.Id)
        && claimed.Outcome == ReviewClaimOutcome.Claimed
        && afterExpiry.Db.Orders[h.Order.Id].ClaimedBy == "sadia";
});

await CheckAsync("reviewer can release a claim", async () =>
{
    var h = await NeedsReview();
    await h.Reviews.ClaimAsync(new ReviewClaimCommand
    {
        MerchantId = merchantId,
        OrderId = h.Order.Id,
        Reviewer = "rafi",
        ClaimFor = TimeSpan.FromMinutes(5),
    });

    var released = await h.Reviews.ReleaseAsync(new ReviewReleaseCommand
    {
        MerchantId = merchantId,
        OrderId = h.Order.Id,
        Reviewer = "rafi",
    });

    return released.Outcome == ReviewReleaseOutcome.Released
        && h.Db.Orders[h.Order.Id].ClaimedBy is null
        && h.Db.Events.Any(e => e.Type == "review.released" && e.ActorName == "rafi");
});

await CheckAsync("review outcome confirms through the workflow and clears the claim", async () =>
{
    var h = await NeedsReview();
    await h.Reviews.ClaimAsync(new ReviewClaimCommand
    {
        MerchantId = merchantId,
        OrderId = h.Order.Id,
        Reviewer = "rafi",
        ClaimFor = TimeSpan.FromMinutes(5),
    });

    var decided = await h.Reviews.DecideAsync(new ReviewDecisionCommand
    {
        MerchantId = merchantId,
        OrderId = h.Order.Id,
        Reviewer = "rafi",
        Outcome = ReviewDecision.Confirm,
        Reason = "customer confirmed",
    });

    var last = h.Db.Events[^1];

    return decided.Outcome == ReviewDecisionOutcome.Moved
        && h.Db.Orders[h.Order.Id].Status == OrderStatus.Confirmed
        && h.Db.Orders[h.Order.Id].ClaimedBy is null
        && last.Type == "order.confirmed"
        && last.Actor == Actor.Merchant
        && last.ActorName == "rafi";
});

await CheckAsync("review rejection is a person decision", async () =>
{
    var h = await NeedsReview();
    await h.Reviews.ClaimAsync(new ReviewClaimCommand
    {
        MerchantId = merchantId,
        OrderId = h.Order.Id,
        Reviewer = "sadia",
        ClaimFor = TimeSpan.FromMinutes(5),
    });

    var decided = await h.Reviews.DecideAsync(new ReviewDecisionCommand
    {
        MerchantId = merchantId,
        OrderId = h.Order.Id,
        Reviewer = "sadia",
        Outcome = ReviewDecision.Reject,
        Reason = "customer said no",
    });

    var last = h.Db.Events[^1];

    return decided.Outcome == ReviewDecisionOutcome.Moved
        && h.Db.Orders[h.Order.Id].Status == OrderStatus.Rejected
        && last.Type == "order.rejected"
        && last.Actor == Actor.Merchant
        && last.ActorName == "sadia";
});

await CheckAsync("review can send an order to call again", async () =>
{
    var h = await NeedsReview();
    await h.Reviews.ClaimAsync(new ReviewClaimCommand
    {
        MerchantId = merchantId,
        OrderId = h.Order.Id,
        Reviewer = "rafi",
        ClaimFor = TimeSpan.FromMinutes(5),
    });

    var decided = await h.Reviews.DecideAsync(new ReviewDecisionCommand
    {
        MerchantId = merchantId,
        OrderId = h.Order.Id,
        Reviewer = "rafi",
        Outcome = ReviewDecision.CallAgain,
        Reason = "call later",
    });

    return decided.Outcome == ReviewDecisionOutcome.Moved
        && h.Db.Orders[h.Order.Id].Status == OrderStatus.Calling
        && h.Db.Orders[h.Order.Id].ClaimedBy is null
        && h.Db.Events[^1].Type == "order.call_again";
});

// ---------------------------------------------------------------------------
// Merchant onboarding and API keys
// ---------------------------------------------------------------------------
MerchantService MerchantHarness(MemoryDatabase db, ISecretProtector? protector = null) =>
    new(new MemoryMerchantStore(db), protector ?? new PrefixProtector(), new FixedClock(now));

static AesGcmSecretProtector AesProtector(string active, bool includeOld = false)
{
    var keys = new Dictionary<string, byte[]>(StringComparer.Ordinal)
    {
        ["k1"] = Key(1),
        ["old"] = Key(2),
    };

    if (active == "new" || includeOld) keys["new"] = Key(3);
    if (!keys.ContainsKey(active)) keys[active] = Key(4);

    return new AesGcmSecretProtector(active, keys);
}

static byte[] Key(byte seed)
{
    var bytes = new byte[32];
    for (var i = 0; i < bytes.Length; i++) bytes[i] = (byte)(seed + i);
    return bytes;
}

await CheckAsync("a merchant can be created with a subscription", async () =>
{
    var db = new MemoryDatabase();
    var service = MerchantHarness(db);

    var r = await service.CreateAsync(new CreateMerchantCommand
    {
        Name = "New Shop",
        Msisdn = "01711223344",
        Calls = true,
        Payments = true,
        Plan = "trial",
    });

    return r.Outcome == CreateMerchantOutcome.Created
        && r.Merchant!.Msisdn == "8801711223344"
        && db.Subscriptions[r.Merchant.Id].Calls
        && db.Subscriptions[r.Merchant.Id].Payments;
});

await CheckAsync("issuing a key shows the secret once and stores only the cipher", async () =>
{
    var db = new MemoryDatabase();
    var service = MerchantHarness(db);
    var created = await service.CreateAsync(new CreateMerchantCommand
    {
        Name = "Key Shop",
        Msisdn = "01711223344",
        Payments = true,
    });

    var issued = await service.IssueKeyAsync(created.Merchant!.Id, "WooCommerce", bootstrapOnly: true);
    var key = issued.Credential!;

    return issued.Outcome == IssueApiKeyOutcome.Issued
        && issued.Secret is { Length: > 20 }
        && key.KeyId.StartsWith("mp_", StringComparison.Ordinal)
        && key.SecretCipher != issued.Secret
        && key.KeyRingId == PrefixProtector.KeyRing
        && key.Label == "WooCommerce";
});

await CheckAsync("the issued secret signs requests for that merchant", async () =>
{
    var db = new MemoryDatabase();
    var protector = new PrefixProtector();
    var service = MerchantHarness(db, protector);
    var created = await service.CreateAsync(new CreateMerchantCommand
    {
        Name = "Signed Shop",
        Msisdn = "01711223344",
        Calls = true,
    });
    var issued = await service.IssueKeyAsync(created.Merchant!.Id, "site", bootstrapOnly: true);
    var stored = await new MemoryMerchantStore(db).FindCredentialAsync(issued.Credential!.KeyId);
    var opened = protector.Unprotect(stored!.SecretCipher, stored.KeyRingId);
    var signature = RequestSignature.Sign(opened, "POST", "/v1/orders", 1790269500, "n1", "{}");

    return opened == issued.Secret
        && stored.TenantId == created.Merchant.Id
        && RequestSignature.Verify(issued.Secret!, "POST", "/v1/orders", 1790269500, "n1", "{}",
            signature, 1790269500);
});

await CheckAsync("key lists never expose secrets", async () =>
{
    var db = new MemoryDatabase();
    var service = MerchantHarness(db);
    var created = await service.CreateAsync(new CreateMerchantCommand
    {
        Name = "List Shop",
        Msisdn = "01711223344",
    });
    var issued = await service.IssueKeyAsync(created.Merchant!.Id, "first", bootstrapOnly: true);
    var listed = await service.ListKeysAsync(created.Merchant.Id);

    return listed.Count == 1
        && listed[0].KeyId == issued.Credential!.KeyId
        && listed[0].SecretCipher.StartsWith("sealed:", StringComparison.Ordinal);
});

await CheckAsync("revoking one key makes only that key unusable", async () =>
{
    var db = new MemoryDatabase();
    var store = new MemoryMerchantStore(db);
    var service = MerchantHarness(db);
    var created = await service.CreateAsync(new CreateMerchantCommand
    {
        Name = "Revoke Shop",
        Msisdn = "01711223344",
    });
    var first = await service.IssueKeyAsync(created.Merchant!.Id, "first", bootstrapOnly: true);
    var second = await service.IssueKeyAsync(created.Merchant.Id, "second");

    var revoked = await service.RevokeKeyAsync(created.Merchant.Id, first.Credential!.KeyId);

    return revoked
        && await store.FindCredentialAsync(first.Credential.KeyId) is null
        && await store.FindCredentialAsync(second.Credential!.KeyId) is not null;
});

await CheckAsync("bootstrap key issue is only for the first key", async () =>
{
    var db = new MemoryDatabase();
    var service = MerchantHarness(db);
    var created = await service.CreateAsync(new CreateMerchantCommand
    {
        Name = "Bootstrap Shop",
        Msisdn = "01711223344",
    });

    var first = await service.IssueKeyAsync(created.Merchant!.Id, "first", bootstrapOnly: true);
    var again = await service.IssueKeyAsync(created.Merchant.Id, "again", bootstrapOnly: true);

    return first.Outcome == IssueApiKeyOutcome.Issued
        && again.Outcome == IssueApiKeyOutcome.Refused;
});

await CheckAsync("webhook register sends a signed test and records delivery", async () =>
{
    var db = new MemoryDatabase();
    var protector = new PrefixProtector();
    var sender = new RecordingWebhookSender(true);
    var merchantService = MerchantHarness(db, protector);
    var created = await merchantService.CreateAsync(new CreateMerchantCommand
    {
        Name = "Hook Shop",
        Msisdn = "01711223344",
    });
    var webhooks = new WebhookService(new MemoryMerchantStore(db), protector, sender, new FixedClock(now));
    var registered = await webhooks.RegisterAsync(created.Merchant!.Id, "https://shop.example.com/hook");
    var test = await webhooks.SendTestAsync(created.Merchant.Id);
    var endpoint = db.Webhooks[created.Merchant.Id];

    var opened = protector.Unprotect(endpoint.SecretCipher, endpoint.KeyRingId);

    return registered.Outcome == WebhookOutcome.Created
        && test.Outcome == WebhookTestOutcome.Delivered
        && endpoint.LastDeliveredAt == now
        && endpoint.LastFailureReason is null
        && sender.LastUrl == "https://shop.example.com/hook"
        && RequestSignature.VerifyWebhook(opened, sender.LastSignature!, sender.LastBody!, now.ToUnixTimeSeconds());
});

await CheckAsync("webhook rejects local and private URLs", async () =>
{
    var db = new MemoryDatabase();
    var service = MerchantHarness(db);
    var created = await service.CreateAsync(new CreateMerchantCommand
    {
        Name = "Blocked Hook Shop",
        Msisdn = "01711223344",
    });
    var webhooks = new WebhookService(
        new MemoryMerchantStore(db), new PrefixProtector(), new RecordingWebhookSender(true), new FixedClock(now));

    var local = await webhooks.RegisterAsync(created.Merchant!.Id, "http://localhost/hook");
    var privateIp = await webhooks.RegisterAsync(created.Merchant.Id, "https://192.168.1.20/hook");

    return local.Outcome == WebhookOutcome.Invalid
        && privateIp.Outcome == WebhookOutcome.Invalid
        && db.Webhooks.IsEmpty;
});

await CheckAsync("webhook rotation makes old signatures fail after grace", async () =>
{
    var db = new MemoryDatabase();
    var protector = new PrefixProtector();
    var service = MerchantHarness(db, protector);
    var created = await service.CreateAsync(new CreateMerchantCommand
    {
        Name = "Rotate Hook Shop",
        Msisdn = "01711223344",
    });
    var webhooks = new WebhookService(
        new MemoryMerchantStore(db), protector, new RecordingWebhookSender(true), new FixedClock(now));

    await webhooks.RegisterAsync(created.Merchant!.Id, "https://shop.example.com/hook");
    var oldEndpoint = db.Webhooks[created.Merchant.Id];
    var oldSecret = protector.Unprotect(oldEndpoint.SecretCipher, oldEndpoint.KeyRingId);
    var oldHeader = RequestSignature.SignWebhook(oldSecret, now.ToUnixTimeSeconds(), "{}");

    var rotated = await webhooks.RotateSecretAsync(created.Merchant.Id);
    var newEndpoint = db.Webhooks[created.Merchant.Id];
    var newSecret = protector.Unprotect(newEndpoint.SecretCipher, newEndpoint.KeyRingId);

    return rotated.Outcome == WebhookOutcome.Updated
        && newSecret != oldSecret
        && !RequestSignature.VerifyWebhook(newSecret, oldHeader, "{}", now.ToUnixTimeSeconds());
});

await CheckAsync("webhook test failure records the reason", async () =>
{
    var db = new MemoryDatabase();
    var protector = new PrefixProtector();
    var service = MerchantHarness(db, protector);
    var created = await service.CreateAsync(new CreateMerchantCommand
    {
        Name = "Fail Hook Shop",
        Msisdn = "01711223344",
    });
    var webhooks = new WebhookService(
        new MemoryMerchantStore(db), protector, new RecordingWebhookSender(false), new FixedClock(now));

    await webhooks.RegisterAsync(created.Merchant!.Id, "https://shop.example.com/hook");
    var test = await webhooks.SendTestAsync(created.Merchant.Id);

    return test.Outcome == WebhookTestOutcome.Failed
        && db.Webhooks[created.Merchant.Id].LastDeliveredAt is null
        && db.Webhooks[created.Merchant.Id].LastFailureReason == "boom";
});

Check("AES-GCM secret protector round-trips with the active key id in the cipher", () =>
{
    var protector = AesProtector("k1");
    var cipher = protector.Protect("merchant-secret", out var keyRingId);

    return keyRingId == "k1"
        && cipher.StartsWith($"{AesGcmSecretProtector.Format}.k1.", StringComparison.Ordinal)
        && cipher != "merchant-secret"
        && protector.Unprotect(cipher, keyRingId) == "merchant-secret";
});

Check("AES-GCM secret protector detects tampering", () =>
{
    var protector = AesProtector("k1");
    var cipher = protector.Protect("merchant-secret", out var keyRingId);
    var firstPayloadDot = cipher.IndexOf('.', AesGcmSecretProtector.Format.Length + 1);
    var tamperAt = firstPayloadDot + 1;
    var tampered = cipher[..tamperAt] + (cipher[tamperAt] == 'A' ? 'B' : 'A') + cipher[(tamperAt + 1)..];

    try
    {
        protector.Unprotect(tampered, keyRingId);
        return false;
    }
    catch (CryptographicException)
    {
        return true;
    }
});

Check("AES-GCM rotation keeps old-key decrypt and uses the new active key", () =>
{
    var oldRing = AesProtector("old");
    var oldCipher = oldRing.Protect("kept", out var oldKeyId);
    var rotated = AesProtector("new", includeOld: true);
    var newCipher = rotated.Protect("fresh", out var newKeyId);

    return oldKeyId == "old"
        && newKeyId == "new"
        && rotated.Unprotect(oldCipher, oldKeyId) == "kept"
        && rotated.Unprotect(newCipher, newKeyId) == "fresh";
});

Check("plaintext development secrets migrate into AES-GCM ciphers", () =>
{
    var db = new MemoryDatabase();
    var merchantIdForSecret = Guid.CreateVersion7();
    db.Credentials["mp_plain"] = new ApiCredential
    {
        TenantId = merchantIdForSecret,
        KeyId = "mp_plain",
        SecretCipher = "plain-secret",
        KeyRingId = PlaintextSecretProtector.KeyRing,
        Label = "legacy",
    };
    db.Webhooks[merchantIdForSecret] = new WebhookEndpoint
    {
        TenantId = merchantIdForSecret,
        Url = "https://shop.example.com/hook",
        SecretCipher = "hook-secret",
        KeyRingId = PlaintextSecretProtector.KeyRing,
    };

    var protector = AesProtector("k1");
    var migrated = SecretCipherMigration.MigratePlaintext(db, protector);

    var credential = db.Credentials["mp_plain"];
    var webhook = db.Webhooks[merchantIdForSecret];

    return migrated == 2
        && credential.KeyRingId == "k1"
        && webhook.KeyRingId == "k1"
        && credential.SecretCipher != "plain-secret"
        && webhook.SecretCipher != "hook-secret"
        && protector.Unprotect(credential.SecretCipher, credential.KeyRingId) == "plain-secret"
        && protector.Unprotect(webhook.SecretCipher, webhook.KeyRingId) == "hook-secret";
});

// ---------------------------------------------------------------------------
// Moving an order
// ---------------------------------------------------------------------------
async Task<(MemoryDatabase Db, OrderTransitionService Move, Order Order)> Placed(
    bool calls = true, bool pay = true, bool webhook = true)
{
    var h = Harness(calls: calls, pay: pay, webhook: webhook);
    var r = await h.Create.CreateAsync(Cmd());

    return (h.Db, h.Move, r.Order!);
}

TransitionCommand Move(Guid id, OrderStatus to, Actor by, string type = "order.changed") =>
    new() { MerchantId = merchantId, OrderId = id, To = to, By = by, EventType = type };

await CheckAsync("a confirmation is recorded and told to the shop", async () =>
{
    var (db, move, order) = await Placed();
    var r = await move.ApplyAsync(Move(order.Id, OrderStatus.Confirmed, Actor.Machine, "order.confirmed"));

    return r.Outcome == TransitionOutcome.Moved
        && r.Order!.Status == OrderStatus.Confirmed
        && r.Order.ConfirmedAt is not null
        && db.Outbox.Count == 1
        && db.Outbox[0].EventType == "order.confirmed";
});

// At-least-once delivery means a webhook arrives twice. The second one has to be silent.
await CheckAsync("asking for the state it already has does nothing", async () =>
{
    var (db, move, order) = await Placed();
    await move.ApplyAsync(Move(order.Id, OrderStatus.Confirmed, Actor.Machine, "order.confirmed"));
    var again = await move.ApplyAsync(Move(order.Id, OrderStatus.Confirmed, Actor.Machine, "order.confirmed"));

    return again.Outcome == TransitionOutcome.Unchanged
        && db.Outbox.Count == 1
        && db.Events.Count == 2;   // received + confirmed, not three
});

await CheckAsync("a worker cannot reject an order that went for review", async () =>
{
    var (db, move, order) = await Placed();
    await move.ApplyAsync(Move(order.Id, OrderStatus.NeedsHuman, Actor.Machine, "order.needs_human"));

    var r = await move.ApplyAsync(Move(order.Id, OrderStatus.Rejected, Actor.Machine));

    return r.Outcome == TransitionOutcome.Refused
        && db.Orders[order.Id].Status == OrderStatus.NeedsHuman;
});

await CheckAsync("but the merchant can", async () =>
{
    var (_, move, order) = await Placed();
    await move.ApplyAsync(Move(order.Id, OrderStatus.NeedsHuman, Actor.Machine, "order.needs_human"));

    var r = await move.ApplyAsync(Move(order.Id, OrderStatus.Rejected, Actor.Merchant, "order.rejected"));

    return r.Outcome == TransitionOutcome.Moved;
});

await CheckAsync("a mis-press can be undone by the merchant", async () =>
{
    var (_, move, order) = await Placed();
    await move.ApplyAsync(Move(order.Id, OrderStatus.Rejected, Actor.Machine, "order.rejected"));

    var undo = await move.ApplyAsync(Move(order.Id, OrderStatus.Confirmed, Actor.Merchant, "order.confirmed"));

    return undo.Outcome == TransitionOutcome.Moved && undo.Order!.Status == OrderStatus.Confirmed;
});

await CheckAsync("a shipped order cannot be cancelled, and says why", async () =>
{
    var (_, move, order) = await Placed();
    await move.ApplyAsync(Move(order.Id, OrderStatus.Confirmed, Actor.Machine));
    await move.ApplyAsync(Move(order.Id, OrderStatus.AwaitingPayment, Actor.Machine));
    await move.ApplyAsync(Move(order.Id, OrderStatus.Paid, Actor.Machine));
    await move.ApplyAsync(Move(order.Id, OrderStatus.Booked, Actor.Machine));
    await move.ApplyAsync(Move(order.Id, OrderStatus.Shipped, Actor.Machine));

    var r = await move.ApplyAsync(Move(order.Id, OrderStatus.Cancelled, Actor.Shop));

    return r.Outcome == TransitionOutcome.Refused
        && r.Reason is not null
        && r.Reason.Contains("shipped", StringComparison.OrdinalIgnoreCase);
});

// Being told an order started ringing trains people to ignore the webhook.
await CheckAsync("intermediate steps are not sent to the shop", async () =>
{
    var (db, move, order) = await Placed();
    await move.ApplyAsync(Move(order.Id, OrderStatus.Confirmed, Actor.Machine));
    await move.ApplyAsync(Move(order.Id, OrderStatus.AwaitingPayment, Actor.Machine));

    return db.Outbox.Count == 1 && db.Outbox[0].EventType == "order.confirmed";
});

await CheckAsync("no endpoint means nothing is queued", async () =>
{
    var (db, move, order) = await Placed(webhook: false);
    await move.ApplyAsync(Move(order.Id, OrderStatus.Confirmed, Actor.Machine));

    return db.Outbox.Count == 0;
});

await CheckAsync("a claim is dropped once the order is decided", async () =>
{
    var (db, move, order) = await Placed();

    db.Orders[order.Id].ClaimedBy = "rafi";
    db.Orders[order.Id].ClaimedUntil = now.AddMinutes(5);

    await move.ApplyAsync(Move(order.Id, OrderStatus.Confirmed, Actor.Merchant));

    return db.Orders[order.Id].ClaimedBy is null && db.Orders[order.Id].ClaimedUntil is null;
});

await CheckAsync("another merchant cannot touch this order", async () =>
{
    var (_, move, order) = await Placed();

    var r = await move.ApplyAsync(new TransitionCommand
    {
        MerchantId = Guid.CreateVersion7(),
        OrderId = order.Id,
        To = OrderStatus.Cancelled,
        By = Actor.Merchant,
    });

    return r.Outcome == TransitionOutcome.NotFound;
});

await CheckAsync("every move leaves an audit row naming who made it", async () =>
{
    var (db, move, order) = await Placed();
    await move.ApplyAsync(Move(order.Id, OrderStatus.NeedsHuman, Actor.Machine, "order.needs_human"));

    await move.ApplyAsync(new TransitionCommand
    {
        MerchantId = merchantId, OrderId = order.Id, To = OrderStatus.Confirmed,
        By = Actor.Merchant, ActorName = "shohag", EventType = "order.confirmed",
    });

    var last = db.Events[^1];

    return db.Events.Count == 3
        && last.Actor == Actor.Merchant
        && last.ActorName == "shohag"
        && last.From == OrderStatus.NeedsHuman
        && last.To == OrderStatus.Confirmed;
});

// ---------------------------------------------------------------------------
// Phase 23: dialler claims
// ---------------------------------------------------------------------------
MemoryDatabase DiallerDb()
{
    var db = new MemoryDatabase();
    db.Merchants[merchantId] = new Merchant
    {
        Id = merchantId,
        TenantId = merchantId,
        Name = "Dial Shop",
        Msisdn = "8801711111111",
        TimeZone = "Asia/Dhaka",
    };
    db.Subscriptions[merchantId] = new Subscription
    {
        TenantId = merchantId,
        Calls = true,
    };

    return db;
}

await CheckAsync("dialler does not claim outside Dhaka calling hours on a UTC server", async () =>
{
    var db = DiallerDb();
    var order = AddOrder(db, "CALL-1", OrderStatus.Calling, now);
    var store = new MemoryOrderStore(db);
    var twoAmDhaka = new DateTimeOffset(2026, 9, 28, 20, 0, 0, TimeSpan.Zero);

    var claimed = await store.ClaimDueCallsAsync(
        "dial-a", 10, twoAmDhaka, twoAmDhaka.AddMinutes(5));

    return claimed.Count == 0
        && db.Orders[order.Id].CallAttempts == 0
        && db.Orders[order.Id].ClaimedBy is null;
});

await CheckAsync("two diallers never claim the same order", async () =>
{
    var db = DiallerDb();
    var order = AddOrder(db, "CALL-2", OrderStatus.Calling, now);
    var store = new MemoryOrderStore(db);

    var first = await store.ClaimDueCallsAsync("dial-a", 10, now, now.AddMinutes(5));
    var second = await store.ClaimDueCallsAsync("dial-b", 10, now.AddSeconds(1), now.AddMinutes(6));

    return first.Single().Order.Id == order.Id
        && second.Count == 0
        && db.Orders[order.Id].CallAttempts == 0
        && db.Orders[order.Id].ClaimedBy == "dial-a";
});

await CheckAsync("a dead dialler's call claim expires", async () =>
{
    var db = DiallerDb();
    var order = AddOrder(db, "CALL-3", OrderStatus.Calling, now);
    var store = new MemoryOrderStore(db);

    await store.ClaimDueCallsAsync("dial-a", 10, now, now.AddMinutes(1));
    var reclaimed = await store.ClaimDueCallsAsync("dial-b", 10, now.AddMinutes(2), now.AddMinutes(7));

    return reclaimed.Single().Order.Id == order.Id
        && db.Orders[order.Id].CallAttempts == 0
        && db.Orders[order.Id].ClaimedBy == "dial-b";
});

await CheckAsync("confirmed and rejected orders are never dialled", async () =>
{
    var db = DiallerDb();
    AddOrder(db, "CALL-CONFIRMED", OrderStatus.Confirmed, now);
    AddOrder(db, "CALL-REJECTED", OrderStatus.Rejected, now);
    var due = AddOrder(db, "CALL-DUE", OrderStatus.Calling, now);
    var store = new MemoryOrderStore(db);

    var claimed = await store.ClaimDueCallsAsync("dial-a", 10, now, now.AddMinutes(5));

    return claimed.Count == 1 && claimed[0].Order.Id == due.Id;
});

await CheckAsync("a received order claimed for calling does not count as an attempt yet", async () =>
{
    var db = DiallerDb();
    var order = AddOrder(db, "CALL-4", OrderStatus.Received, now);
    var store = new MemoryOrderStore(db);

    var claimed = await store.ClaimDueCallsAsync("dial-a", 10, now, now.AddMinutes(5));
    var current = db.Orders[order.Id];
    var callEvent = db.Events.Single(e => e.OrderId == order.Id && e.Type == "call.claimed");

    return claimed.Single().Order.Id == order.Id
        && current.Status == OrderStatus.Calling
        && current.CallAttempts == 0
        && callEvent.From == OrderStatus.Received
        && callEvent.To == OrderStatus.Calling;
});

await CheckAsync("a placed call records one attempt and the attempt time", async () =>
{
    var db = DiallerDb();
    var order = AddOrder(db, "CALL-5", OrderStatus.Calling, now);
    var store = new MemoryOrderStore(db);

    await store.RecordCallAttemptAsync(merchantId, order.Id, now.AddMinutes(1));
    var current = db.Orders[order.Id];
    var callEvent = db.Events.Single(e => e.OrderId == order.Id && e.Type == "call.attempted");

    return current.CallAttempts == 1
        && current.LastCallAttemptAt == now.AddMinutes(1)
        && current.NextCallAttemptAt is null
        && callEvent.Detail == "1";
});

await CheckAsync("a retry is not claimed before the redial gap has passed", async () =>
{
    var db = DiallerDb();
    var order = AddOrder(db, "CALL-6", OrderStatus.Calling, now);
    var store = new MemoryOrderStore(db);

    await store.RecordCallAttemptAsync(merchantId, order.Id, now);
    var next = new RedialPolicy().NextAttemptAt(
        db.Orders[order.Id].CallAttempts,
        db.Orders[order.Id].LastCallAttemptAt,
        new CallingHours(),
        now.AddMinutes(1));
    await store.ScheduleNextCallAsync(
        merchantId, order.Id, next!.Value, now.AddMinutes(1), "call ended with no keypress");

    var early = await store.ClaimDueCallsAsync("dial-a", 10, now.AddMinutes(19), now.AddMinutes(24));
    var due = await store.ClaimDueCallsAsync("dial-b", 10, now.AddMinutes(20), now.AddMinutes(25));

    return early.Count == 0
        && due.Single().Order.Id == order.Id
        && db.Orders[order.Id].NextCallAttemptAt == next;
});

Check("attempts stop at the maximum and land in review, never rejected", () =>
{
    var policy = new RedialPolicy();
    var last = now.AddHours(-1);
    var next = policy.NextAttemptAt(policy.MaxAttempts, last, new CallingHours(), now);
    var target = OrderLifecycle.FromCallOutcome(CallOutcome.NoAnswer);

    return next is null
        && target == OrderStatus.NeedsHuman
        && target != OrderStatus.Rejected;
});

Check("a redial scheduled outside the window waits for the morning", () =>
{
    var hours = new CallingHours();
    var policy = new RedialPolicy();
    var lastAttempt = new DateTimeOffset(2026, 9, 28, 20, 30, 0, CallingHours.DhakaOffset);
    var nowLate = new DateTimeOffset(2026, 9, 28, 20, 35, 0, CallingHours.DhakaOffset);

    var next = policy.NextAttemptAt(2, lastAttempt, hours, nowLate);

    return next is { } n
        && n.ToOffset(CallingHours.DhakaOffset).Hour == 9
        && n.ToOffset(CallingHours.DhakaOffset).Day == 29;
});

// ---------------------------------------------------------------------------
// Phase 25: call outcomes
// ---------------------------------------------------------------------------
CallOutcomeCommand Outcome(Guid orderId, Guid callSessionId, CallOutcome? outcome) =>
    new()
    {
        MerchantId = merchantId,
        OrderId = orderId,
        CallSessionId = callSessionId,
        Outcome = outcome,
    };

await CheckAsync("phase 25: pressing 1 confirms through the order workflow", async () =>
{
    var h = CallOutcomeHarness();
    var order = AddOrder(h.Db, "OUTCOME-1", OrderStatus.Calling, now);
    var session = Guid.Parse("01929999-0000-7000-8000-000000000251");

    var result = await h.Outcomes.ApplyAsync(Outcome(order.Id, session, CallOutcome.Confirmed));
    var events = h.Db.Events.Where(e => e.OrderId == order.Id).ToList();

    return result.Kind == CallOutcomeApplyKind.Moved
        && h.Db.Orders[order.Id].Status == OrderStatus.Confirmed
        && h.Db.Orders[order.Id].Digit == "1"
        && events.Any(e => e.Type == "call.ended" && e.Detail == "call ended with keypress")
        && events.Any(e => e.Type == "order.confirmed")
        && h.Db.Outbox.Count == 1
        && h.Db.Outbox[0].EventType == "order.confirmed";
});

await CheckAsync("phase 25: pressing 0 records the customer's rejection", async () =>
{
    var h = CallOutcomeHarness();
    var order = AddOrder(h.Db, "OUTCOME-0", OrderStatus.Calling, now);

    var result = await h.Outcomes.ApplyAsync(Outcome(
        order.Id,
        Guid.Parse("01929999-0000-7000-8000-000000000250"),
        CallOutcome.Rejected));

    return result.Kind == CallOutcomeApplyKind.Moved
        && h.Db.Orders[order.Id].Status == OrderStatus.Rejected
        && h.Db.Orders[order.Id].Digit == "0"
        && h.Db.Events.Any(e => e.OrderId == order.Id && e.Type == "call.ended")
        && h.Db.Outbox.Single().EventType == "order.rejected";
});

await CheckAsync("phase 25: pressing 9 sends the order to a person", async () =>
{
    var h = CallOutcomeHarness();
    var order = AddOrder(h.Db, "OUTCOME-9", OrderStatus.Calling, now);

    var result = await h.Outcomes.ApplyAsync(Outcome(
        order.Id,
        Guid.Parse("01929999-0000-7000-8000-000000000259"),
        CallOutcome.NeedsHuman));

    return result.Kind == CallOutcomeApplyKind.Moved
        && h.Db.Orders[order.Id].Status == OrderStatus.NeedsHuman
        && h.Db.Orders[order.Id].Digit == "9"
        && h.Db.Outbox.Single().EventType == "order.needs_human";
});

await CheckAsync("phase 25: silence after the last attempt is review, not rejected", async () =>
{
    var h = CallOutcomeHarness();
    var order = AddOrder(h.Db, "OUTCOME-SILENCE", OrderStatus.Calling, now);
    order.CallAttempts = new RedialPolicy().MaxAttempts;
    order.LastCallAttemptAt = now.AddHours(-3);

    var result = await h.Outcomes.ApplyAsync(Outcome(
        order.Id,
        Guid.Parse("01929999-0000-7000-8000-000000000253"),
        null));

    return result.Kind == CallOutcomeApplyKind.Moved
        && h.Db.Orders[order.Id].Status == OrderStatus.NeedsHuman
        && h.Db.Orders[order.Id].Status != OrderStatus.Rejected
        && h.Db.Orders[order.Id].Digit is null
        && h.Db.Events.Any(e => e.OrderId == order.Id
            && e.Type == "call.ended"
            && e.Detail == "call ended with no keypress");
});

await CheckAsync("phase 25: applying the same call outcome twice is silent the second time", async () =>
{
    var h = CallOutcomeHarness();
    var order = AddOrder(h.Db, "OUTCOME-IDEMPOTENT", OrderStatus.Calling, now);
    var session = Guid.Parse("01929999-0000-7000-8000-000000000255");

    await h.Outcomes.ApplyAsync(Outcome(order.Id, session, CallOutcome.Confirmed));
    await h.Outcomes.ApplyAsync(Outcome(order.Id, session, CallOutcome.Confirmed));

    return h.Db.Events.Count(e => e.OrderId == order.Id && e.Type == "call.ended") == 1
        && h.Db.Events.Count(e => e.OrderId == order.Id && e.Type == "order.confirmed") == 1
        && h.Db.Outbox.Count == 1
        && h.Db.Outbox[0].EventType == "order.confirmed";
});

await CheckAsync("phase 25: a machine still cannot reject an order waiting for review", async () =>
{
    var (db, move, order) = await Placed();
    await move.ApplyAsync(Move(order.Id, OrderStatus.NeedsHuman, Actor.Machine, "order.needs_human"));

    var refused = await move.ApplyAsync(Move(order.Id, OrderStatus.Rejected, Actor.Machine));

    return refused.Outcome == TransitionOutcome.Refused
        && refused.Reason == "Only a person can reject an order that was sent for review."
        && db.Orders[order.Id].Status == OrderStatus.NeedsHuman;
});

// ---------------------------------------------------------------------------
// Phase 30: merchant trunk resolution
// ---------------------------------------------------------------------------
TrunkResolver TrunkResolver(MemoryDatabase db) =>
    new(new MemorySipTrunkStore(db), new TelephonyRoutingOptions(
        "09610000000",
        "shared-trunk",
        null,
        null));

SipTrunk Trunk(Guid tenant, string host, string callerId, bool active = true, string? endpointName = null) => new()
{
    TenantId = tenant,
    ProviderName = "BTRC IPTSP",
    Host = host,
    EndpointName = endpointName,
    CallerId = callerId,
    IsActive = active,
    CreatedAt = now,
    UpdatedAt = now,
};

await CheckAsync("phase 30: a merchant with no trunk uses the shared trunk", async () =>
{
    var db = DiallerDb();
    var order = AddOrder(db, "TRUNK-SHARED", OrderStatus.Calling, now, msisdn: "8801711223344");

    var route = await TrunkResolver(db).ResolveAsync(order);

    return route.CallerId == "09610000000"
        && route.Endpoint == "PJSIP/8801711223344@shared-trunk";
});

await CheckAsync("phase 30: the legacy endpoint template remains a fallback", async () =>
{
    var db = DiallerDb();
    var order = AddOrder(db, "TRUNK-TEMPLATE", OrderStatus.Calling, now, msisdn: "8801711223344");
    var resolver = new TrunkResolver(new MemorySipTrunkStore(db), new TelephonyRoutingOptions(
        "09610000000",
        "shared-trunk",
        null,
        "PJSIP/{msisdn}@template-trunk"));

    var route = await resolver.ResolveAsync(order);

    return route.CallerId == "09610000000"
        && route.Endpoint == "PJSIP/8801711223344@template-trunk";
});

await CheckAsync("phase 30: a disabled merchant trunk is never used", async () =>
{
    var db = DiallerDb();
    var order = AddOrder(db, "TRUNK-DISABLED", OrderStatus.Calling, now, msisdn: "8801711223344");
    db.SipTrunks[Guid.CreateVersion7()] = Trunk(merchantId, "disabled-trunk", "09612222222", active: false);

    var route = await TrunkResolver(db).ResolveAsync(order);

    return route.CallerId == "09610000000"
        && route.Endpoint == "PJSIP/8801711223344@shared-trunk";
});

await CheckAsync("phase 30: a merchant trunk controls caller id and endpoint", async () =>
{
    var db = DiallerDb();
    var order = AddOrder(db, "TRUNK-MERCHANT", OrderStatus.Calling, now, msisdn: "8801711223344");
    db.SipTrunks[Guid.CreateVersion7()] = Trunk(
        merchantId,
        "sip.same-provider.example",
        "09613333333",
        endpointName: "merchant-trunk");

    var route = await TrunkResolver(db).ResolveAsync(order);

    return route.CallerId == "09613333333"
        && route.Endpoint == "PJSIP/8801711223344@merchant-trunk";
});

await CheckAsync("phase 30: a trunk without endpoint name falls back to host", async () =>
{
    var db = DiallerDb();
    var order = AddOrder(db, "TRUNK-HOST", OrderStatus.Calling, now, msisdn: "8801711223344");
    db.SipTrunks[Guid.CreateVersion7()] = Trunk(merchantId, "host-trunk", "09614444444");

    var route = await TrunkResolver(db).ResolveAsync(order);

    return route.CallerId == "09614444444"
        && route.Endpoint == "PJSIP/8801711223344@host-trunk";
});

// ---------------------------------------------------------------------------
// Signing
// ---------------------------------------------------------------------------
const string secret = "sxJx3Xp1G0s1kM8oZ4qYv2mB6nC9dF7hK0lP3rT5uW8=";

Check("the canonical string has five lines and an upper-case digest", () =>
{
    var lines = RequestSignature.Canonical("POST", "/v1/orders", 1790269500, "n1", "{}").Split('\n');

    return lines.Length == 5 && lines[0] == "POST" && lines[1] == "/v1/orders"
        && lines[4].Length == 64 && lines[4] == lines[4].ToUpperInvariant();
});

Check("a signature verifies against itself", () =>
{
    var s = RequestSignature.Sign(secret, "POST", "/v1/orders", 1790269500, "n1", "{\"a\":1}");
    return RequestSignature.Verify(secret, "POST", "/v1/orders", 1790269500, "n1", "{\"a\":1}", s, 1790269500);
});

Check("hex case does not matter", () =>
{
    var s = RequestSignature.Sign(secret, "POST", "/v1/orders", 1790269500, "n1", "{}");
    return RequestSignature.Verify(secret, "POST", "/v1/orders", 1790269500, "n1", "{}",
        s.ToLowerInvariant(), 1790269500);
});

// Without the method inside the MAC, a signed GET replays as a DELETE.
Check("changing the method breaks it", () =>
{
    var s = RequestSignature.Sign(secret, "GET", "/v1/orders/x", 1790269500, "n1", "");
    return !RequestSignature.Verify(secret, "DELETE", "/v1/orders/x", 1790269500, "n1", "", s, 1790269500);
});

Check("changing the path breaks it", () =>
{
    var s = RequestSignature.Sign(secret, "GET", "/v1/orders/mine", 1790269500, "n1", "");
    return !RequestSignature.Verify(secret, "GET", "/v1/orders/yours", 1790269500, "n1", "", s, 1790269500);
});

Check("one edited byte of body breaks it", () =>
{
    var s = RequestSignature.Sign(secret, "POST", "/v1/orders", 1790269500, "n1", "{\"a\":1}");
    return !RequestSignature.Verify(secret, "POST", "/v1/orders", 1790269500, "n1", "{\"a\":2}", s, 1790269500);
});

Check("a stale request is refused though the MAC is right", () =>
{
    var s = RequestSignature.Sign(secret, "POST", "/v1/orders", 1790269500, "n1", "{}");

    return !RequestSignature.Verify(secret, "POST", "/v1/orders", 1790269500, "n1", "{}", s, 1790269500 + 301)
        && RequestSignature.Verify(secret, "POST", "/v1/orders", 1790269500, "n1", "{}", s, 1790269500 + 299);
});

Check("a webhook we signed verifies", () =>
{
    var h = RequestSignature.SignWebhook(secret, 1790269500, "{\"event\":\"order.confirmed\"}");
    return RequestSignature.VerifyWebhook(secret, h, "{\"event\":\"order.confirmed\"}", 1790269500);
});

Check("a webhook body edited after signing is refused", () =>
{
    var h = RequestSignature.SignWebhook(secret, 1790269500, "{\"status\":\"Rejected\"}");
    return !RequestSignature.VerifyWebhook(secret, h, "{\"status\":\"Confirmed\"}", 1790269500);
});

Check("a malformed header is refused rather than throwing", () =>
{
    foreach (var h in new[] { "", "nonsense", "t=,v1=", "v1=abc", "t=1790269500", "t=abc,v1=def" })
    {
        if (RequestSignature.VerifyWebhook(secret, h, "{}", 1790269500)) return false;
    }

    return true;
});

// ---------------------------------------------------------------------------
// Agreement with YoPay
//
// Not "does my HMAC work" - that is covered above - but "do two independent
// implementations produce the same bytes". These vectors came out of YoPay's server.
// ---------------------------------------------------------------------------
MoynaPay.Check.Vectors.Run(Check);

// ---------------------------------------------------------------------------
// The two modules that came over from YoPay and YoVoiceAgent
// ---------------------------------------------------------------------------
MoynaPay.Check.Modules.Run(Check);

// ---------------------------------------------------------------------------
// The holes found in the review of 29 September
//
// Each of these failed before its fix; that is what makes them worth keeping.
// ---------------------------------------------------------------------------
await MoynaPay.Check.Hardening.RunAsync(Check, CheckAsync);

// ---------------------------------------------------------------------------
// Phase 3: telling the shop
//
// The retry ladder, the ordering rule, the lease, and where a delivery is not
// allowed to go - none of which can be seen by hand, because all of it is about
// time and repetition.
// ---------------------------------------------------------------------------
await MoynaPay.Check.Outbox.RunAsync(Check, CheckAsync);

// ---------------------------------------------------------------------------
// Phase 19: the ARI event stream, and whose channel is whose
// ---------------------------------------------------------------------------
MoynaPay.Check.Telephony.Run(Check);

// ---------------------------------------------------------------------------
// Replay
// ---------------------------------------------------------------------------
await CheckAsync("a nonce cannot be used twice", async () =>
{
    var store = new MemoryNonceStore(new FixedClock(now));

    return await store.TryUseAsync("k1", "n1")
        && !await store.TryUseAsync("k1", "n1")
        && await store.TryUseAsync("k2", "n1");   // a different key, its own space
});

Console.WriteLine();
Console.WriteLine(fail == 0 ? $"all {pass} checks passed" : $"{pass} passed, {fail} FAILED");
return fail == 0 ? 0 : 1;

sealed class FixedClock(DateTimeOffset at) : IClock
{
    private DateTimeOffset utcNow = at;

    public DateTimeOffset UtcNow => utcNow;

    public void Set(DateTimeOffset value) => utcNow = value;
}

sealed class PrefixProtector : ISecretProtector
{
    public const string KeyRing = "test-prefix";

    public string Protect(string plaintext, out string keyRingId)
    {
        keyRingId = KeyRing;
        return "sealed:" + plaintext;
    }

    public string Unprotect(string cipher, string keyRingId) =>
        keyRingId == KeyRing && cipher.StartsWith("sealed:", StringComparison.Ordinal)
            ? cipher["sealed:".Length..]
            : throw new InvalidOperationException("bad cipher");
}

sealed class FakeConversationModel(string response) : IConversationModel
{
    public string ProviderName => "fake-llm";

    public string? LastUserText { get; private set; }

    public async IAsyncEnumerable<ModelChunk> RespondAsync(
        ConversationState state,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        LastUserText = state.History.LastOrDefault(t => t.Role == "user")?.Text;

        await Task.Yield();
        ct.ThrowIfCancellationRequested();
        yield return new TextDelta(response);
        yield return new ModelCompleted("stop");
    }
}

sealed class FakeRecordingArchive : IRecordingArchive
{
    private readonly Dictionary<string, byte[]> _objects = new(StringComparer.Ordinal);

    public string? LastRecordingName { get; private set; }

    public string? LastObjectStorageKey { get; private set; }

    public Task<RecordingArchiveResult> ArchiveAsync(
        string asteriskRecordingName,
        string objectStorageKey,
        CancellationToken ct = default)
    {
        LastRecordingName = asteriskRecordingName;
        LastObjectStorageKey = objectStorageKey;
        _objects[objectStorageKey] = [1, 2, 3];

        return Task.FromResult(new RecordingArchiveResult(objectStorageKey, 12_345, 42));
    }

    public Task<bool> ExistsAsync(string objectStorageKey, CancellationToken ct = default) =>
        Task.FromResult(_objects.ContainsKey(objectStorageKey));

    public Task<bool> DeleteAsync(string objectStorageKey, CancellationToken ct = default) =>
        Task.FromResult(_objects.Remove(objectStorageKey));
}

sealed class RecordingWebhookSender(bool succeeds) : IWebhookSender
{
    public string? LastUrl { get; private set; }
    public string? LastBody { get; private set; }
    public string? LastSignature { get; private set; }

    public Task<WebhookSendResult> SendAsync(WebhookEndpoint endpoint, string body, string signature,
        CancellationToken ct = default)
    {
        LastUrl = endpoint.Url;
        LastBody = body;
        LastSignature = signature;

        return Task.FromResult(succeeds
            ? new WebhookSendResult(true, 200, null)
            : new WebhookSendResult(false, null, "boom"));
    }

    /// <summary>The outbox half of the port. The phase 6 checks do not use it.</summary>
    public Task<MoynaPay.Application.Outbox.DeliveryAttempt> DeliverAsync(
        WebhookDelivery delivery, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(delivery);

        LastUrl = delivery.Url;
        LastBody = delivery.Body;

        return Task.FromResult(succeeds
            ? MoynaPay.Application.Outbox.DeliveryAttempt.Answered(200)
            : MoynaPay.Application.Outbox.DeliveryAttempt.Unreachable("boom"));
    }
}
