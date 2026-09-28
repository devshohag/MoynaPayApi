using System.Security.Cryptography;
using MoynaPay.Application.Abstractions;
using MoynaPay.Application.Merchants;
using MoynaPay.Application.Orders;
using MoynaPay.Application.Security;
using MoynaPay.Application.Workflows;
using MoynaPay.Domain.Merchants;
using MoynaPay.Domain.Orders;
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

CreateOrderCommand Cmd(string reference = "ORD-1", string msisdn = "01711223344", decimal amount = 1250m)
    => new()
    {
        MerchantId = merchantId, Reference = reference, Msisdn = msisdn,
        CustomerName = "সাদিয়া আক্তার", Amount = amount, Summary = "দুইটি শার্ট",
    };

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
    var tampered = cipher[..^1] + (cipher[^1] == 'A' ? 'B' : 'A');

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
    public DateTimeOffset UtcNow { get; } = at;
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
}
