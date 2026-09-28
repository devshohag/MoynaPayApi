using MoynaPay.Infrastructure;

// Turns bKash notifications into paid orders.
//
// The merchant's phone uploads what bKash told it; this reads those messages, works out
// what each one was, and matches it against an open invoice. Its own process because the
// work is a queue being drained under locks, not a request being answered.
//
// Phase 4 fills it, from the parser and matcher already written in YoPay.

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddMoynaPay(builder.Configuration);
builder.Services.AddHostedService<PaymentWorker>();

var app = builder.Build();

if (DependencyInjection.IsInMemory(app.Configuration))
{
    app.Logger.LogWarning("In-memory stores. Nothing here survives a restart. Development only.");
}

app.MapGet("/health", () => Results.Ok(new { status = "ok", service = "MoynaPay.Worker.Payments" }));

app.Run();

/// <summary>
/// Reads raw device messages, parses them, and settles what it can.
///
/// Two properties decide whether this is worth anything, and both are why it is a worker
/// rather than a handler:
///
/// A message is claimed by exactly one pass. Two workers taking the same row would settle
/// one payment against two invoices, which is money.
///
/// Nothing is ever settled twice. The same notification arrives again whenever a phone
/// comes back online with a queue, and the second one must do nothing at all.
/// </summary>
internal sealed class PaymentWorker(ILogger<PaymentWorker> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        log.LogInformation("Payment worker started. Matching arrives in phase 4.");

        while (!ct.IsCancellationRequested)
        {
            try
            {
                // Phase 4: claim a batch of raw events, parse, match on wallet + exact
                // amount inside the window, settle, and write why when nothing matched.
                // A message that cannot be read goes to review, never to the bin.
            }
            catch (Exception ex)
            {
                log.LogError(ex, "Payment tick failed");
            }

            await Task.Delay(TimeSpan.FromSeconds(2), ct).ConfigureAwait(false);
        }
    }
}
