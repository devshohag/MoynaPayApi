using MoynaPay.Application.Abstractions;
using MoynaPay.Application.Orders;
using MoynaPay.Application.Voice;
using MoynaPay.Application.Voice.Ari;
using MoynaPay.Application.Workflows;
using MoynaPay.Domain.Merchants;
using MoynaPay.Domain.Orders;
using MoynaPay.Infrastructure;
using MoynaPay.Infrastructure.Memory;
using MoynaPay.Infrastructure.Voice;
using System.Collections.Concurrent;

// Rings customers and brings back what they pressed.
//
// This one has to be its own process, not a choice: it holds a long-lived event stream open
// to Asterisk and reacts to what happens on it. A web host answers requests; this listens.

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddMoynaPay(builder.Configuration, builder.Environment.IsDevelopment());
builder.Services.AddScoped<CreateOrderService>();
builder.Services.AddScoped<OrderTransitionService>();
builder.Services.AddScoped<OrderWorkflowService>();
builder.Services.AddSingleton<VoiceHealth>();
builder.Services.AddHostedService<VoiceWorker>();

var app = builder.Build();

if (DependencyInjection.IsInMemory(app.Configuration))
{
    app.Logger.LogWarning("In-memory stores. Nothing here survives a restart. Development only.");
}

if (app.Environment.IsDevelopment())
{
    app.MapPost("/dev/calls/softphone", async (
        DevSoftphoneCallRequest? request,
        MemoryDatabase db,
        AriClient ari,
        CallCorrelator calls,
        IClock clock,
        IConfiguration configuration,
        CancellationToken ct) =>
    {
        var merchantId = Guid.Parse("01929999-0000-7000-8000-000000000001");
        var now = clock.UtcNow;

        db.Merchants[merchantId] = new Merchant
        {
            Id = merchantId,
            TenantId = merchantId,
            Name = request?.ShopName ?? "Demo Store",
            Msisdn = "8801711111111",
            Status = MerchantStatus.Trial,
            CreatedAt = now,
            UpdatedAt = now,
        };

        db.Subscriptions[merchantId] = new Subscription
        {
            TenantId = merchantId,
            Calls = true,
            Payments = false,
            Courier = false,
            Plan = "local",
            CreatedAt = now,
            UpdatedAt = now,
        };

        var reference = request?.Reference ?? $"LOCAL-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}";
        var order = new Order
        {
            TenantId = merchantId,
            Reference = reference,
            CustomerName = request?.CustomerName ?? "Local Caller",
            Msisdn = "8801711223344",
            Amount = request?.Amount ?? 1250m,
            Summary = request?.Summary ?? "test order",
            Status = OrderStatus.Calling,
            CreatedAt = now,
            UpdatedAt = now,
        };

        db.Orders[order.Id] = order;
        db.Events.Add(new OrderEvent
        {
            TenantId = merchantId,
            OrderId = order.Id,
            Type = "order.received",
            Actor = Actor.Shop,
            To = OrderStatus.Calling,
            At = now,
        });

        var session = Guid.CreateVersion7();
        var channelId = calls.Register(
            session,
            merchantId,
            order.Id,
            request?.ShopName ?? "Demo Store",
            now);

        var endpoint = request?.Endpoint
            ?? configuration["Telephony:DevSoftphoneEndpoint"]
            ?? "PJSIP/1001";

        await ari.OriginateEndpointAsync(
            session,
            channelId,
            request?.FromNumber ?? "09610000000",
            endpoint,
            ct).ConfigureAwait(false);

        return Results.Accepted($"/dev/orders/{reference}", new
        {
            reference,
            orderId = order.Id,
            callSessionId = session,
            channelId,
            endpoint,
        });
    });

    app.MapGet("/dev/orders/{reference}", (
        string reference,
        MemoryDatabase db) =>
    {
        var merchantId = Guid.Parse("01929999-0000-7000-8000-000000000001");
        var order = db.Orders.Values.FirstOrDefault(o =>
            o.TenantId == merchantId
            && string.Equals(o.Reference, reference, StringComparison.Ordinal));

        return order is null
            ? Results.NotFound()
            : Results.Ok(new
            {
                order.Reference,
                order.Status,
                order.Digit,
                order.Reason,
                order.ConfirmedAt,
                events = db.Events
                    .Where(e => e.TenantId == merchantId && e.OrderId == order.Id)
                    .Select(e => new { e.Type, e.From, e.To, e.Actor, e.Detail, e.At })
                    .ToList(),
            });
    });
}

// Not just "the process is up". A voice worker whose socket to Asterisk is down looks
// perfectly healthy from the outside and cannot ring anybody, which is the failure that goes
// unnoticed longest.
app.MapGet("/health", (VoiceHealth health) => Results.Ok(new
{
    status = health.Connected ? "ok" : "degraded",
    service = "MoynaPay.Worker.Voice",
    asterisk = health.Connected ? "connected" : "disconnected",
    liveCalls = health.LiveCalls,
}));

app.Run();

/// <summary>
/// Whether the socket to Asterisk is up, readable by the health endpoint.
/// </summary>
internal sealed class VoiceHealth
{
    public bool Connected { get; set; }

    public int LiveCalls { get; set; }
}

/// <summary>
/// Phase 19: the event stream, and knowing which call a channel belongs to.
///
/// What this does today is listen, recognise and forget. Answering, playing the script and
/// reading a keypress are phases 20 to 25; they hang off the switch below, and every one of
/// them needs the answer this phase provides first - which call is this?
///
/// The loop does nothing slow. A handler that blocks here stops every event for every call,
/// so when the later phases arrive their work belongs on its own task, not in this switch.
/// </summary>
internal sealed class VoiceWorker(
    AriEventStream stream,
    AriClient ari,
    CallCorrelator calls,
    DtmfCollector keypresses,
    IPromptVoice voice,
    IOrderStore orders,
    IServiceScopeFactory scopes,
    VoiceHealth health,
    ILogger<VoiceWorker> log) : BackgroundService
{
    /// <summary>The conversation in progress on each channel.</summary>
    private readonly ConcurrentDictionary<string, CallFlow> _flows = new(StringComparer.Ordinal);

    /// <summary>
    /// The answer timers. One per channel, cancelled the moment a key arrives - a timeout
    /// that fires after the customer already answered would play the repeat over the top of
    /// the closing line.
    /// </summary>
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _waits =
        new(StringComparer.Ordinal);

    /// <summary>
    /// PlaybackFinished names the playback, and not every Asterisk event repeats the
    /// channel. Remembering who started the playback lets the flow keep moving after audio.
    /// </summary>
    private readonly ConcurrentDictionary<string, string> _playbacks =
        new(StringComparer.Ordinal);

    /// <summary>
    /// The script the keys are read against. Per merchant from phase 21; the defaults are
    /// the ones that matter here - 1 confirms, 0 rejects, 9 asks for a person.
    /// </summary>
    private static readonly CallScript Script = CallScript.Default;

    /// <summary>
    /// How long a registered channel may go unseen before it is assumed never to have
    /// existed. An originate that fails outright leaves a registration nothing will release,
    /// and without this the live-call count slowly becomes a lie.
    /// </summary>
    private static readonly TimeSpan Abandoned = TimeSpan.FromMinutes(10);

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        log.LogInformation("Voice worker started, listening at {Uri}", Redact(stream.BuildUri()));

        stream.ConnectionChanged += state =>
        {
            health.Connected = state == AriConnectionState.Connected;

            if (state == AriConnectionState.Connected) log.LogInformation("ARI connected");
            else log.LogWarning("ARI disconnected");
        };

        stream.MalformedEvent += frame =>
            log.LogWarning("ARI sent a frame that did not parse ({Length} bytes)", frame.Length);

        using var sweeper = new Timer(_ => Sweep(), null, Abandoned, Abandoned);

        await foreach (var evt in stream.ReadAsync(ct).ConfigureAwait(false))
        {
            try
            {
                await HandleAsync(evt, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // One event must never end the stream. The alternative is a worker that goes
                // quiet on a single odd channel and stops ringing anybody.
                log.LogError(ex, "ARI event {Type} failed", evt.Type);
            }

            health.LiveCalls = calls.Count;
        }

        log.LogInformation("Voice worker stopped");
    }

    private async Task HandleAsync(AriEvent evt, CancellationToken ct)
    {
        switch (evt.Type)
        {
            case AriEvent.StasisStart:
                await StartedAsync(evt, ct).ConfigureAwait(false);
                break;

            case AriEvent.ChannelDtmfReceived:
                await PressedAsync(evt, ct).ConfigureAwait(false);
                break;

            case AriEvent.StasisEnd:
            case AriEvent.ChannelDestroyed:
                StopWaiting(evt.ChannelId ?? "");
                ForgetPlaybacks(evt.ChannelId);

                if (evt.ChannelId is not null && _flows.TryRemove(evt.ChannelId, out var finished))
                {
                    finished.Ended();
                }

                var answer = keypresses.Forget(evt.ChannelId);

                if (calls.ReleaseByChannel(evt.ChannelId) is { } ended)
                {
                    await ApplyOutcomeAsync(ended, answer, ct).ConfigureAwait(false);

                    log.LogInformation(
                        "Call {CallSessionId} for order {OrderId} ended as {Outcome}",
                        ended.CallSessionId, ended.OrderId,
                        answer?.ToString() ?? "no answer");
                }

                break;

            case AriEvent.PlaybackFinished:
                var channelId = evt.ChannelId;

                if (channelId is null && evt.PlaybackId is not null)
                {
                    _playbacks.TryRemove(evt.PlaybackId, out channelId);
                }
                else if (evt.PlaybackId is not null)
                {
                    _playbacks.TryRemove(evt.PlaybackId, out _);
                }

                if (channelId is not null && _flows.TryGetValue(channelId, out var playing))
                {
                    await ActAsync(channelId, playing.Said(), ct).ConfigureAwait(false);
                }

                break;

            // Phases 26 to 29 attach here.
            case AriEvent.RecordingFinished:
            default:
                break;
        }
    }

    private async Task StartedAsync(AriEvent evt, CancellationToken ct)
    {
        if (evt.ChannelId is null) return;

        // The ordinary path: this is a channel id we chose ourselves before placing the
        // call, so there is nothing to look up and nothing to race.
        if (calls.TryGetByChannel(evt.ChannelId, out var known))
        {
            await AnswerAsync(evt.ChannelId, known, ct).ConfigureAwait(false);
            return;
        }

        // The fallback: an Asterisk that ignored the supplied channel id. The variable was
        // set on the originate for exactly this, and it costs one request on a path that
        // should never run.
        var raw = await ari
            .GetVariableAsync(evt.ChannelId, AriClient.SessionVariable, ct).ConfigureAwait(false);

        if (Guid.TryParse(raw, out var sessionId)
            && calls.TryAdopt(evt.ChannelId, sessionId, out var adopted))
        {
            log.LogWarning(
                "Channel {ChannelId} arrived under a different id and was matched by variable to call {CallSessionId}",
                evt.ChannelId, adopted.CallSessionId);

            await AnswerAsync(evt.ChannelId, adopted, ct).ConfigureAwait(false);
            return;
        }

        // Not ours. An inbound call, or another application sharing the Stasis name. Saying
        // so is worth a line: a flood of these means the Stasis app name is shared with
        // something it should not be.
        log.LogInformation("Channel {ChannelId} entered Stasis and is not a call we placed",
            evt.ChannelId);
    }

    /// <summary>
    /// Picks the call up and starts listening for a key.
    ///
    /// Listening begins before the channel is answered, not after. Asterisk delivers the
    /// answer and the first keypress on the same socket, and a customer who is already
    /// holding the handset can press 1 in the gap - a keypress that arrives before we are
    /// listening is a confirmation the customer believes they gave.
    /// </summary>
    private async Task AnswerAsync(string channelId, OutboundCall call, CancellationToken ct)
    {
        keypresses.Listen(channelId);

        await ari.AnswerAsync(channelId, ct).ConfigureAwait(false);

        log.LogInformation(
            "Call {CallSessionId} for order {OrderId} answered on channel {ChannelId}",
            call.CallSessionId, call.OrderId, channelId);

        var order = await orders
            .FindByIdAsync(call.MerchantId, call.OrderId, ct).ConfigureAwait(false);

        if (order is null)
        {
            // The order went away between placing the call and it being answered. There is
            // nothing to read out, and reading out a guess is worse than saying nothing.
            log.LogWarning("Order {OrderId} is gone; hanging up", call.OrderId);
            await ari.HangupAsync(channelId, ct).ConfigureAwait(false);
            return;
        }

        var flow = new CallFlow(Script, order, call.ShopName);
        _flows[channelId] = flow;

        await ActAsync(channelId, flow.Begin(), ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Does the one thing the flow asked for. Everything that decides is in CallFlow; this
    /// only knows how to speak, wait and hang up.
    /// </summary>
    private async Task ActAsync(string channelId, CallAction action, CancellationToken ct)
    {
        switch (action.Kind)
        {
            case CallActionKind.Play:
                var media = await voice.MediaForAsync(action.Say!, ct).ConfigureAwait(false);
                var playbackId = await ari.PlayAsync(channelId, media, ct).ConfigureAwait(false);
                _playbacks[playbackId] = channelId;
                break;

            case CallActionKind.Listen:
                StartWaiting(channelId, action.For!.Value);
                break;

            case CallActionKind.Hangup:
                await ari.HangupAsync(channelId, ct).ConfigureAwait(false);
                break;

            case CallActionKind.Nothing:
            default:
                break;
        }
    }

    /// <summary>
    /// Waits for a key, and tells the flow when nobody pressed one.
    ///
    /// On its own task, not on the event loop. Eight seconds of silence on this thread is
    /// eight seconds in which no other call's keypress is heard.
    /// </summary>
    private void StartWaiting(string channelId, TimeSpan forHowLong)
    {
        StopWaiting(channelId);

        var cancel = new CancellationTokenSource();
        _waits[channelId] = cancel;

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(forHowLong, cancel.Token).ConfigureAwait(false);

                if (_flows.TryGetValue(channelId, out var flow))
                {
                    await ActAsync(channelId, flow.Waited(), CancellationToken.None)
                        .ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                // A key arrived first. That is the ordinary ending, not a fault.
            }
            catch (Exception ex)
            {
                log.LogError(ex, "Answer timeout on channel {ChannelId} failed", channelId);
            }
        }, CancellationToken.None);
    }

    private void StopWaiting(string channelId)
    {
        if (_waits.TryRemove(channelId, out var existing))
        {
            existing.Cancel();
            existing.Dispose();
        }
    }

    private void ForgetPlaybacks(string? channelId)
    {
        if (string.IsNullOrEmpty(channelId)) return;

        foreach (var playback in _playbacks)
        {
            if (string.Equals(playback.Value, channelId, StringComparison.Ordinal))
            {
                _playbacks.TryRemove(playback.Key, out _);
            }
        }
    }

    private async Task PressedAsync(AriEvent evt, CancellationToken ct)
    {
        if (evt.ChannelId is null) return;
        if (!calls.TryGetByChannel(evt.ChannelId, out var call)) return;

        var press = keypresses.Press(evt.ChannelId, evt.Read("digit"), Script, DateTimeOffset.UtcNow);

        switch (press.Outcome)
        {
            case KeypressOutcome.Accepted:
            case KeypressOutcome.Exhausted:
                log.LogInformation(
                    "Order {OrderId}: customer pressed a key meaning {Decision}",
                    call.OrderId, press.Decision);

                StopWaiting(evt.ChannelId);

                if (_flows.TryGetValue(evt.ChannelId, out var flow))
                {
                    await ActAsync(evt.ChannelId, flow.Pressed(press.Decision!.Value), ct)
                        .ConfigureAwait(false);
                }

                break;

            case KeypressOutcome.Unknown:
                log.LogInformation(
                    "Order {OrderId}: a key with no meaning, {Count} so far",
                    call.OrderId, press.UnknownPresses);
                break;

            case KeypressOutcome.Ignored:
            default:
                break;
        }
    }

    private async Task ApplyOutcomeAsync(OutboundCall call, CallOutcome? outcome, CancellationToken ct)
    {
        var final = outcome ?? CallOutcome.NoAnswer;
        var target = OrderLifecycle.FromCallOutcome(final);

        await using var scope = scopes.CreateAsyncScope();
        var workflow = scope.ServiceProvider.GetRequiredService<OrderWorkflowService>();

        var result = await workflow.DecideAsync(new WorkflowDecisionCommand
        {
            MerchantId = call.MerchantId,
            OrderId = call.OrderId,
            To = target,
            By = Actor.Machine,
            Reason = final == CallOutcome.NoAnswer ? "call ended with no keypress" : "call ended with keypress",
            Digit = final switch
            {
                CallOutcome.Confirmed => "1",
                CallOutcome.Rejected => "0",
                CallOutcome.NeedsHuman => "9",
                _ => null,
            },
            EventType = target switch
            {
                OrderStatus.Confirmed => "order.confirmed",
                OrderStatus.Rejected => "order.rejected",
                OrderStatus.NeedsHuman => "order.needs_human",
                _ => "order.changed",
            },
        }, ct).ConfigureAwait(false);

        if (result.Outcome is TransitionOutcome.Refused or TransitionOutcome.NotFound)
        {
            log.LogWarning(
                "Call {CallSessionId} outcome {Outcome} could not move order {OrderId}: {Reason}",
                call.CallSessionId, final, call.OrderId, result.Reason ?? result.Outcome.ToString());
        }
    }

    private void Sweep()
    {
        foreach (var call in calls.Sweep(DateTimeOffset.UtcNow, Abandoned))
        {
            log.LogWarning(
                "Call {CallSessionId} for order {OrderId} was placed and never seen; forgetting it",
                call.CallSessionId, call.OrderId);
        }

        health.LiveCalls = calls.Count;
    }

    /// <summary>The socket url carries the ARI password, so it is never logged whole.</summary>
    private static string Redact(Uri uri) =>
        $"{uri.Scheme}://{uri.Authority}{uri.AbsolutePath}";
}

internal sealed record DevSoftphoneCallRequest(
    string? Endpoint,
    string? FromNumber,
    string? Reference,
    string? ShopName,
    string? CustomerName,
    decimal? Amount,
    string? Summary);
