using System.Net;
using MoynaPay.Application.Abstractions;
using MoynaPay.Application.Outbox;
using MoynaPay.Application.Security;
using MoynaPay.Domain.Merchants;
using MoynaPay.Domain.Orders;
using MoynaPay.Infrastructure.Memory;

namespace MoynaPay.Check;

/// <summary>
/// Phase 3: telling the shop.
///
/// Everything here would otherwise be checked by waiting - a retry ladder that spans a day
/// and a half, a shop that answers on the fourth try, a lease that expires because a worker
/// died. None of that is observable by hand, and all of it is where a delivery system goes
/// wrong, so the whole thing runs against a clock that is a variable and a sender that is a
/// lambda.
/// </summary>
internal static class Outbox
{
    public static async Task RunAsync(
        Action<string, Func<bool>> check, Func<string, Func<Task<bool>>, Task> checkAsync)
    {
        ArgumentNullException.ThrowIfNull(check);
        ArgumentNullException.ThrowIfNull(checkAsync);

        RetryLadder(check);
        Urls(check);
        await Deliveries(checkAsync).ConfigureAwait(false);
    }

    // -----------------------------------------------------------------------
    // The retry ladder
    // -----------------------------------------------------------------------
    private static void RetryLadder(Action<string, Func<bool>> check)
    {
        var now = new DateTimeOffset(2026, 9, 28, 10, 0, 0, TimeSpan.Zero);

        check("a 200 is delivered", () =>
            DeliveryPolicy.Plan(0, DeliveryAttempt.Answered(200), now).State == DeliveryState.Delivered);

        check("a 204 is delivered", () =>
            DeliveryPolicy.Plan(0, DeliveryAttempt.Answered(204), now).State == DeliveryState.Delivered);

        check("a 299 is delivered", () =>
            DeliveryPolicy.Plan(0, DeliveryAttempt.Answered(299), now).State == DeliveryState.Delivered);

        check("a 300 is not delivered", () =>
            DeliveryPolicy.Plan(0, DeliveryAttempt.Answered(302), now).State == DeliveryState.Retry);

        check("the first retry is ten seconds away", () =>
            DeliveryPolicy.Plan(0, DeliveryAttempt.Answered(500), now).NextAttemptAt
                == now.AddSeconds(10));

        check("the second retry is a minute away", () =>
            DeliveryPolicy.Plan(1, DeliveryAttempt.Answered(500), now).NextAttemptAt
                == now.AddMinutes(1));

        check("the gaps only ever widen", () =>
        {
            for (var i = 1; i < DeliveryPolicy.Gaps.Length; i++)
            {
                if (DeliveryPolicy.Gaps[i] <= DeliveryPolicy.Gaps[i - 1]) return false;
            }

            return true;
        });

        check("the last attempt gives up", () =>
            DeliveryPolicy.Plan(DeliveryPolicy.MaxAttempts - 1, DeliveryAttempt.Answered(500), now)
                .State == DeliveryState.Dead);

        check("the attempt before the last still retries", () =>
            DeliveryPolicy.Plan(DeliveryPolicy.MaxAttempts - 2, DeliveryAttempt.Answered(500), now)
                .State == DeliveryState.Retry);

        check("giving up says how many times we tried", () =>
            DeliveryPolicy.Plan(DeliveryPolicy.MaxAttempts - 1, DeliveryAttempt.Answered(500), now)
                .Reason?.Contains($"{DeliveryPolicy.MaxAttempts} attempts", StringComparison.Ordinal) == true);

        // 410 is the one answer a shop can give that means "stop", and it has to work on
        // the first attempt - a shop that retired an endpoint should not be posted to for
        // another thirty hours.
        check("a 410 stops immediately", () =>
            DeliveryPolicy.Plan(0, DeliveryAttempt.Answered(410), now).State == DeliveryState.Dead);

        // A 404 is NOT a 410. A shop with a broken route answers 404 while it is broken and
        // 200 once somebody fixes it; giving up on the first one throws away every order
        // placed during the outage.
        check("a 404 is retried, not abandoned", () =>
            DeliveryPolicy.Plan(0, DeliveryAttempt.Answered(404), now).State == DeliveryState.Retry);

        check("a 401 is retried", () =>
            DeliveryPolicy.Plan(0, DeliveryAttempt.Answered(401), now).State == DeliveryState.Retry);

        check("no answer at all is retried", () =>
            DeliveryPolicy.Plan(0, DeliveryAttempt.Unreachable("no answer"), now).State
                == DeliveryState.Retry);

        check("the whole ladder is about a day and a half", () =>
        {
            var total = TimeSpan.Zero;
            foreach (var gap in DeliveryPolicy.Gaps) total += gap;

            return total > TimeSpan.FromHours(24) && total < TimeSpan.FromHours(48);
        });

        check("the lease outlasts the request timeout by a margin", () =>
            DeliveryPolicy.Lease > TimeSpan.FromSeconds(60));
    }

    // -----------------------------------------------------------------------
    // Where a delivery may go
    // -----------------------------------------------------------------------
    private static void Urls(Action<string, Func<bool>> check)
    {
        check("an ordinary https url is allowed", () =>
            WebhookUrl.Refuse("https://shop.example.com/hooks/moynapay") is null);

        check("plain http is allowed", () =>
            WebhookUrl.Refuse("http://shop.example.com/hooks") is null);

        check("a non-standard port is allowed", () =>
            WebhookUrl.Refuse("https://shop.example.com:8443/hooks") is null);

        check("an empty url is refused", () => WebhookUrl.Refuse("") is not null);
        check("a relative url is refused", () => WebhookUrl.Refuse("/hooks") is not null);
        check("file:// is refused", () => WebhookUrl.Refuse("file:///etc/passwd") is not null);

        check("localhost is refused", () => WebhookUrl.Refuse("http://localhost:6379/") is not null);
        check("127.0.0.1 is refused", () => WebhookUrl.Refuse("http://127.0.0.1/hooks") is not null);
        check("10.x is refused", () => WebhookUrl.Refuse("http://10.0.0.5:6379/") is not null);
        check("192.168.x is refused", () => WebhookUrl.Refuse("http://192.168.1.1/") is not null);
        check("172.16.x is refused", () => WebhookUrl.Refuse("http://172.20.0.1/") is not null);
        check("172.32.x is public", () => WebhookUrl.Refuse("http://172.32.0.1/") is null);

        // The cloud metadata address. Reaching it from inside a hosted network hands over
        // the instance's credentials, which is the single most valuable thing on the box.
        check("the link-local range is refused", () =>
            WebhookUrl.Refuse("http://169.254.169.254/latest/meta-data/") is not null);

        check("::1 is refused", () => WebhookUrl.Refuse("http://[::1]/hooks") is not null);

        // The same private address, written so a check that only reads IPv4 misses it.
        check("an ipv4-mapped private address is refused", () =>
            WebhookUrl.IsPrivate(IPAddress.Parse("::ffff:10.0.0.1")));

        check("a public address is not private", () =>
            !WebhookUrl.IsPrivate(IPAddress.Parse("203.0.113.10")));

        check(".local is refused", () => WebhookUrl.Refuse("http://printer.local/hooks") is not null);
    }

    // -----------------------------------------------------------------------
    // The dispatcher, end to end, with no network
    // -----------------------------------------------------------------------
    private static async Task Deliveries(Func<string, Func<Task<bool>>, Task> checkAsync)
    {
        await checkAsync("a delivery is signed so the shop can verify it", async () =>
        {
            var world = new World();
            world.Queue("order.paid");

            string? presented = null, body = null;
            long stamp = 0;

            world.Sender.Reply = d =>
            {
                var (signature, _, _) = DeliveryHeaders.Build(d);
                presented = signature;
                body = d.Body;
                stamp = d.Timestamp;

                return DeliveryAttempt.Answered(200);
            };

            await world.RunAsync();

            return presented is not null
                && RequestSignature.VerifyWebhook(World.Secret, presented, body!, stamp);
        });

        await checkAsync("the delivery id survives every retry", async () =>
        {
            var world = new World();
            world.Queue("order.paid");

            var ids = new List<Guid>();
            world.Sender.Reply = d => { ids.Add(d.DeliveryId); return DeliveryAttempt.Answered(500); };

            // Four passes, each after the gap the previous one asked for.
            for (var i = 0; i < 4; i++)
            {
                await world.RunAsync();
                world.Clock.Advance(DeliveryPolicy.Gaps[i]);
            }

            return ids.Count == 4 && ids.Distinct().Count() == 1;
        });

        await checkAsync("a message is not sent again before its gap has passed", async () =>
        {
            var world = new World();
            world.Queue("order.paid");
            world.Sender.Reply = _ => DeliveryAttempt.Answered(500);

            await world.RunAsync();

            world.Clock.Advance(TimeSpan.FromSeconds(9));
            var early = await world.RunAsync();

            world.Clock.Advance(TimeSpan.FromSeconds(2));
            var due = await world.RunAsync();

            return early.Claimed == 0 && due.Claimed == 1;
        });

        await checkAsync("a shop that recovers is delivered to", async () =>
        {
            var world = new World();
            world.Queue("order.paid");

            var calls = 0;
            world.Sender.Reply = _ => ++calls < 3
                ? DeliveryAttempt.Unreachable("connection refused")
                : DeliveryAttempt.Answered(200);

            for (var i = 0; i < 3; i++)
            {
                await world.RunAsync();
                world.Clock.Advance(DeliveryPolicy.Gaps[i]);
            }

            var message = world.Db.Outbox[0];

            return message.DeliveredAt is not null && !message.IsDead && message.Attempts == 3;
        });

        await checkAsync("a delivered message is never sent twice", async () =>
        {
            var world = new World();
            world.Queue("order.paid");

            var calls = 0;
            world.Sender.Reply = _ => { calls++; return DeliveryAttempt.Answered(200); };

            await world.RunAsync();
            world.Clock.Advance(TimeSpan.FromHours(1));
            await world.RunAsync();

            return calls == 1;
        });

        await checkAsync("a shop that never answers is given up on, not retried forever", async () =>
        {
            var world = new World();
            world.Queue("order.paid");
            world.Sender.Reply = _ => DeliveryAttempt.Unreachable("no answer");

            for (var i = 0; i < DeliveryPolicy.MaxAttempts + 3; i++)
            {
                await world.RunAsync();
                world.Clock.Advance(TimeSpan.FromHours(48));
            }

            var message = world.Db.Outbox[0];

            return message.IsDead
                && message.Attempts == DeliveryPolicy.MaxAttempts
                && message.DeliveredAt is null;
        });

        // The rule the whole product's credibility rests on: a shop told "paid" before
        // "confirmed" writes nonsense into its own database and blames us.
        await checkAsync("an order's messages leave in the order they were written", async () =>
        {
            var world = new World();
            var order = world.Queue("order.confirmed").OrderId;
            world.QueueFor(order, "order.paid");
            world.QueueFor(order, "order.shipped");

            var seen = new List<string>();

            // The first one fails once. If the ordering rule is not there, "paid" and
            // "shipped" go out during that window and the shop learns the order was
            // delivered before it learns anybody confirmed it.
            var refusals = 0;

            world.Sender.Reply = d =>
            {
                seen.Add(d.EventType);

                return d.EventType == "order.confirmed" && refusals++ == 0
                    ? DeliveryAttempt.Answered(500)
                    : DeliveryAttempt.Answered(200);
            };

            await world.RunAsync();                                 // confirmed, refused
            world.Clock.Advance(DeliveryPolicy.Gaps[0]);

            for (var i = 0; i < 4; i++) await world.RunAsync();

            return seen.Count == 4
                && seen[0] == "order.confirmed"
                && seen[1] == "order.confirmed"                     // the retry, before anything else
                && seen[2] == "order.paid"
                && seen[3] == "order.shipped";
        });

        await checkAsync("a stuck message holds back the rest of its own order only", async () =>
        {
            var world = new World();

            var slow = world.Queue("order.confirmed").OrderId;
            world.QueueFor(slow, "order.paid");

            var other = world.NewOrder().Id;
            world.QueueFor(other, "order.confirmed");

            var seen = new List<string>();

            world.Sender.Reply = d =>
            {
                seen.Add(d.EventType);

                // The first order's shop is down; the second one's is fine.
                return world.OrderOf(d.DeliveryId) == slow
                    ? DeliveryAttempt.Answered(500)
                    : DeliveryAttempt.Answered(200);
            };

            await world.RunAsync();

            return seen.Count == 2                                  // both heads went out
                && world.OpenFor(other) == 0                        // the healthy one is done
                && world.OpenFor(slow) == 2;                        // the other is still queued
        });

        await checkAsync("a second dispatcher does not take a claimed message", async () =>
        {
            var world = new World();
            world.Queue("order.paid");

            var claimed = await world.Store.ClaimDueAsync(
                "worker-a", 50, world.Clock.UtcNow, world.Clock.UtcNow + DeliveryPolicy.Lease);

            var second = await world.Store.ClaimDueAsync(
                "worker-b", 50, world.Clock.UtcNow, world.Clock.UtcNow + DeliveryPolicy.Lease);

            return claimed.Count == 1 && second.Count == 0;
        });

        await checkAsync("a dispatcher that died does not hold the queue forever", async () =>
        {
            var world = new World();
            world.Queue("order.paid");

            await world.Store.ClaimDueAsync(
                "worker-a", 50, world.Clock.UtcNow, world.Clock.UtcNow + DeliveryPolicy.Lease);

            world.Clock.Advance(DeliveryPolicy.Lease + TimeSpan.FromSeconds(1));

            var second = await world.Store.ClaimDueAsync(
                "worker-b", 50, world.Clock.UtcNow, world.Clock.UtcNow + DeliveryPolicy.Lease);

            return second.Count == 1;
        });

        await checkAsync("a merchant with no endpoint is not posted to unsigned", async () =>
        {
            var world = new World(withEndpoint: false);
            world.Queue("order.paid");

            var sent = 0;
            world.Sender.Reply = _ => { sent++; return DeliveryAttempt.Answered(200); };

            await world.RunAsync();

            return sent == 0
                && world.Db.Outbox[0].IsDead
                && world.Db.Outbox[0].Attempts == 0;
        });

        await checkAsync("a webhook pointed at our own network is refused before it is sent", async () =>
        {
            var world = new World();
            world.Endpoint!.Url = "http://169.254.169.254/latest/meta-data/";
            world.Queue("order.paid");

            var sent = 0;
            world.Sender.Reply = _ => { sent++; return DeliveryAttempt.Answered(200); };

            await world.RunAsync();

            return sent == 0 && world.Db.Outbox[0].IsDead;
        });

        await checkAsync("giving up before knocking does not count as an attempt", async () =>
        {
            var world = new World(withEndpoint: false);
            world.Queue("order.paid");
            world.Sender.Reply = _ => DeliveryAttempt.Answered(200);

            await world.RunAsync();

            // A merchant reading this row must see that we never knocked, not a count that
            // suggests their server was asked and said nothing.
            return world.Db.Outbox[0].Attempts == 0
                && world.Db.Outbox[0].LastFailureReason?.Contains("endpoint", StringComparison.Ordinal) == true;
        });

        await checkAsync("the per-order callback url overrides the merchant's", async () =>
        {
            var world = new World();
            world.Order.CallbackUrl = "https://this-one-order.example.com/hook";
            world.Queue("order.paid");

            string? used = null;
            world.Sender.Reply = d => { used = d.Url; return DeliveryAttempt.Answered(200); };

            await world.RunAsync();

            return used == "https://this-one-order.example.com/hook";
        });

        await checkAsync("a summary counts what actually happened", async () =>
        {
            var world = new World();
            world.Queue("order.paid");
            world.Sender.Reply = _ => DeliveryAttempt.Answered(500);

            var summary = await world.RunAsync();

            return summary is { Claimed: 1, Delivered: 0, Retried: 1, Dead: 0, Failed: 0 };
        });
    }

    // -----------------------------------------------------------------------
    // A merchant, a shop, a queue and a clock that moves when told
    // -----------------------------------------------------------------------
    private sealed class World
    {
        public const string Secret = "whsec_test_0123456789";

        public MemoryDatabase Db { get; } = new();
        public MovingClock Clock { get; } = new(new DateTimeOffset(2026, 9, 28, 10, 0, 0, TimeSpan.Zero));
        public FakeSender Sender { get; } = new();
        public MemoryOutboxStore Store { get; }
        public WebhookEndpoint? Endpoint { get; }
        public Order Order { get; }

        private readonly OutboxDispatcher _dispatcher;
        private readonly Guid _merchant = Guid.CreateVersion7();

        public World(bool withEndpoint = true)
        {
            Store = new MemoryOutboxStore(Db);

            Db.Merchants[_merchant] = new Merchant
            {
                Id = _merchant, TenantId = _merchant, Name = "Test Shop",
                Msisdn = "8801711223344", TimeZone = "Asia/Dhaka",
            };

            if (withEndpoint)
            {
                Endpoint = new WebhookEndpoint
                {
                    TenantId = _merchant,
                    Url = "https://shop.example.com/hooks/moynapay",
                    SecretCipher = Secret,
                    KeyRingId = PlaintextSecretProtector.KeyRing,
                };

                Db.Webhooks[_merchant] = Endpoint;
            }

            Order = NewOrder();

            _dispatcher = new OutboxDispatcher(
                Store,
                new MemoryOrderStore(Db),
                new MemoryMerchantStore(Db),
                new PlaintextSecretProtector(),
                Sender,
                Clock);
        }

        public Task<DispatchSummary> RunAsync() => _dispatcher.RunOnceAsync("check", 50);

        public OutboxMessage Queue(string eventType) => QueueFor(Order.Id, eventType);

        public OutboxMessage QueueFor(Guid orderId, string eventType)
        {
            // Each message is written a tick after the last, the way the transition service
            // writes them: one per state change, never two at the same instant.
            var at = Clock.UtcNow.AddMilliseconds(Db.Outbox.Count);

            var message = new OutboxMessage
            {
                TenantId = _merchant,
                OrderId = orderId,
                EventType = eventType,
                PayloadJson = $$"""{"event":"{{eventType}}","reference":"REF-1"}""",
                NextAttemptAt = Clock.UtcNow,
                CreatedAt = at,
                UpdatedAt = at,
            };

            Db.Outbox.Add(message);
            return message;
        }

        /// <summary>A second order, so "one order waits" can be told from "everything waits".</summary>
        public Order NewOrder()
        {
            var order = new Order
            {
                TenantId = _merchant,
                Reference = $"REF-{Db.Orders.Count + 1}",
                CustomerName = "Customer",
                Msisdn = "8801711223344",
                Amount = 1250m,
            };

            Db.Orders[order.Id] = order;
            return order;
        }

        public Guid OrderOf(Guid deliveryId) =>
            Db.Outbox.First(m => m.Id == deliveryId).OrderId;

        public int OpenFor(Guid orderId) =>
            Db.Outbox.Count(m => m.OrderId == orderId && m.DeliveredAt is null && !m.IsDead);
    }

    private sealed class MovingClock(DateTimeOffset at) : IClock
    {
        public DateTimeOffset UtcNow { get; private set; } = at;

        public void Advance(TimeSpan by) => UtcNow += by;
    }

    private sealed class FakeSender : IWebhookSender
    {
        public Func<WebhookDelivery, DeliveryAttempt> Reply { get; set; } =
            _ => DeliveryAttempt.Answered(200);

        public Task<DeliveryAttempt> DeliverAsync(WebhookDelivery delivery,
            CancellationToken ct = default) =>
            Task.FromResult(Reply(delivery));

        // The other half of the port, used by the phase 6 webhook test. Nothing here calls
        // it; it is present because the dispatcher asks for the whole interface.
        public Task<WebhookSendResult> SendAsync(WebhookEndpoint endpoint, string body,
            string signature, CancellationToken ct = default) =>
            Task.FromResult(new WebhookSendResult(true, 200, null));
    }
}
