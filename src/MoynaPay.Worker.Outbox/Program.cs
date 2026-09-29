using MoynaPay.Application.Outbox;
using MoynaPay.Infrastructure;

// Delivers results to merchants' shops.
//
// Its own process, not a thread inside the API. A shop whose server is slow - and some are
// very slow - must not hold a web request open, and a delivery that has to be retried for a
// day and a half cannot live inside one.

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddMoynaPay(builder.Configuration, builder.Environment.IsDevelopment());
builder.Services.AddHostedService<OutboxWorker>();

var app = builder.Build();

if (DependencyInjection.IsInMemory(app.Configuration))
{
    app.Logger.LogWarning("In-memory stores. Nothing here survives a restart. Development only.");
}

// Workers answer /health for the same reason the API does: something has to be able to tell
// "running" from "running and stuck", and a process with no port cannot be asked.
app.MapGet("/health", () => Results.Ok(new { status = "ok", service = "MoynaPay.Worker.Outbox" }));

app.Run();

/// <summary>
/// Drains the outbox.
///
/// At least once, never exactly once: a network failure after the shop committed looks
/// exactly like one before it, so a delivery may repeat and the shop has to be idempotent.
/// The delivery id stays the same across retries, which is what they deduplicate on.
///
/// The loop itself is deliberately dull. Every decision worth arguing about - what is due,
/// which message may go first, how long to wait, when to stop - lives in OutboxDispatcher
/// and DeliveryPolicy, where it is exercised without a network or a clock that really ticks.
/// </summary>
internal sealed class OutboxWorker(IServiceProvider services, ILogger<OutboxWorker> log)
    : BackgroundService
{
    /// <summary>
    /// Names this process in the claimed_by column. Reading a stuck queue in psql and seeing
    /// which machine is holding a row is worth the one line it costs.
    /// </summary>
    private readonly string _workerId = $"{Environment.MachineName}:{Environment.ProcessId}";

    private static readonly TimeSpan Idle = TimeSpan.FromSeconds(5);

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        log.LogInformation("Outbox worker started as {WorkerId}", _workerId);

        while (!ct.IsCancellationRequested)
        {
            var busy = false;

            try
            {
                // A scope per pass, so each pass gets its own DbContext. Sharing one across
                // the life of the worker would accumulate every message it has ever tracked
                // and answer later passes from that stale cache.
                await using var scope = services.CreateAsyncScope();

                var dispatcher = scope.ServiceProvider.GetRequiredService<OutboxDispatcher>();

                var summary = await dispatcher.RunOnceAsync(_workerId, 50, ct).ConfigureAwait(false);

                busy = !summary.DidNothing;

                if (busy)
                {
                    log.LogInformation(
                        "Outbox pass: {Claimed} claimed, {Delivered} delivered, {Retried} to retry, {Dead} dead, {Failed} unfinished",
                        summary.Claimed, summary.Delivered, summary.Retried, summary.Dead, summary.Failed);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // One bad row must never end the loop. A dispatcher that stops quietly is
                // the worst failure here: nobody notices until a merchant asks why their
                // shop has heard nothing since Tuesday.
                log.LogError(ex, "Outbox pass failed");
            }

            // Straight back round when there was work, because a busy queue means more is
            // waiting and sleeping five seconds between full batches would hold a backlog
            // open for no reason.
            if (busy) continue;

            try
            {
                await Task.Delay(Idle, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        log.LogInformation("Outbox worker stopped");
    }
}
