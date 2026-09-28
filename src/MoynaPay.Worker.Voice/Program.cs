using MoynaPay.Infrastructure;

// Rings customers and brings back what they pressed.
//
// This one has to be its own process, not a choice: it holds a long-lived event stream
// open to Asterisk and reacts to what happens on it. A web host answers requests; this
// listens.
//
// Phase 5 fills it, from the ARI client and event loop already written in YoVoiceAgent.

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddMoynaPay(builder.Configuration);
builder.Services.AddHostedService<VoiceWorker>();

var app = builder.Build();

if (DependencyInjection.IsInMemory(app.Configuration))
{
    app.Logger.LogWarning("In-memory stores. Nothing here survives a restart. Development only.");
}

app.MapGet("/health", () => Results.Ok(new { status = "ok", service = "MoynaPay.Worker.Voice" }));

app.Run();

/// <summary>
/// The dialler and the script.
///
/// What it must get right, in the order it matters:
///
/// Calls go out only inside the merchant's own hours. A confirmation call at two in the
/// morning is a complaint and the end of that merchant's trust.
///
/// The channel it originated is the call it follows. Losing that thread means the answer
/// attaches to a record nobody is reading.
///
/// Silence is not an answer. Two prompts with no usable keypress sends the order to a
/// person - it never becomes a rejection here.
/// </summary>
internal sealed class VoiceWorker(ILogger<VoiceWorker> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        log.LogInformation("Voice worker started. Calling arrives in phase 5.");

        while (!ct.IsCancellationRequested)
        {
            try
            {
                // Phase 5: pick up orders due a call inside the window, originate through
                // the merchant's trunk, play the script, collect a keypress, report the
                // outcome through OrderTransitionService, and space the retries.
            }
            catch (Exception ex)
            {
                log.LogError(ex, "Voice tick failed");
            }

            await Task.Delay(TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);
        }
    }
}
