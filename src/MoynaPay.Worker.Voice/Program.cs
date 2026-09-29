using MoynaPay.Application.Voice.Ari;
using MoynaPay.Infrastructure;
using MoynaPay.Infrastructure.Voice;

// Rings customers and brings back what they pressed.
//
// This one has to be its own process, not a choice: it holds a long-lived event stream open
// to Asterisk and reacts to what happens on it. A web host answers requests; this listens.

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddMoynaPay(builder.Configuration, builder.Environment.IsDevelopment());
builder.Services.AddSingleton<VoiceHealth>();
builder.Services.AddHostedService<VoiceWorker>();

var app = builder.Build();

if (DependencyInjection.IsInMemory(app.Configuration))
{
    app.Logger.LogWarning("In-memory stores. Nothing here survives a restart. Development only.");
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
    VoiceHealth health,
    ILogger<VoiceWorker> log) : BackgroundService
{
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

            case AriEvent.StasisEnd:
            case AriEvent.ChannelDestroyed:
                if (calls.ReleaseByChannel(evt.ChannelId) is { } ended)
                {
                    log.LogInformation(
                        "Call {CallSessionId} for order {OrderId} ended on channel {ChannelId}",
                        ended.CallSessionId, ended.OrderId, ended.ChannelId);
                }

                break;

            // Phases 20 to 25 attach here: the keypress, the end of a prompt, the recording.
            case AriEvent.ChannelDtmfReceived:
            case AriEvent.PlaybackFinished:
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
            log.LogInformation(
                "Call {CallSessionId} for order {OrderId} answered on channel {ChannelId}",
                known.CallSessionId, known.OrderId, known.ChannelId);

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

            return;
        }

        // Not ours. An inbound call, or another application sharing the Stasis name. Saying
        // so is worth a line: a flood of these means the Stasis app name is shared with
        // something it should not be.
        log.LogInformation("Channel {ChannelId} entered Stasis and is not a call we placed",
            evt.ChannelId);
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
