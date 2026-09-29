using MoynaPay.Infrastructure;

// Delivers results to merchants' shops.
//
// Its own process, not a thread inside the API. A shop whose server is slow - and some
// are very slow - must not hold a web request open, and a delivery that has to be retried
// for an hour cannot live inside one.
//
// Phase 2 fills the loop: read the outbox, sign, post, back off, dead-letter.

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddMoynaPay(builder.Configuration, builder.Environment.IsDevelopment());
builder.Services.AddHostedService<OutboxWorker>();

var app = builder.Build();

if (DependencyInjection.IsInMemory(app.Configuration))
{
    app.Logger.LogWarning("In-memory stores. Nothing here survives a restart. Development only.");
}

// Workers answer /health for the same reason the API does: something has to be able to
// tell "running" from "running and stuck", and a process with no port cannot be asked.
app.MapGet("/health", () => Results.Ok(new { status = "ok", service = "MoynaPay.Worker.Outbox" }));

app.Run();

/// <summary>
/// Drains the outbox.
///
/// At least once, never exactly once: a network failure after the shop committed looks
/// exactly like one before it, so a delivery may repeat and the shop has to be idempotent.
/// The delivery id stays the same across retries, which is what they deduplicate on.
/// </summary>
internal sealed class OutboxWorker(ILogger<OutboxWorker> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        log.LogInformation("Outbox worker started. Delivery arrives in phase 2.");

        while (!ct.IsCancellationRequested)
        {
            try
            {
                // Phase 2: due rows, sign with the merchant's webhook secret, POST,
                // widen the gap on failure, stop and mark dead after the last attempt.
            }
            catch (Exception ex)
            {
                // One bad row must never end the loop. A dispatcher that stops quietly is
                // the worst failure here: nobody notices until a merchant asks why their
                // shop has heard nothing since Tuesday.
                log.LogError(ex, "Outbox tick failed");
            }

            await Task.Delay(TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);
        }
    }
}
