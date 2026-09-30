using Microsoft.Extensions.Logging;
using MoynaPay.Application.Abstractions;

namespace MoynaPay.Infrastructure.Voice;

/// <summary>
/// A voice that cannot speak yet.
///
/// Phase 21 decides WHAT to say and when; turning that Bangla into audio is phase 22. Until
/// then the flow has to be runnable end to end, so this writes the line to the log and hands
/// back a second of silence for Asterisk to play.
///
/// It is called what it is on purpose. A class named SimpleVoice or DefaultVoice is one
/// somebody ships, and a confirmation call that plays a second of silence and hangs up is
/// worse than one that never goes out - the customer has been rung, and told nothing.
/// The guard below is what stops that.
/// </summary>
public sealed class UnsynthesisedVoice(ILogger<UnsynthesisedVoice> log, bool isDevelopment)
    : IPromptVoice
{
    public const string Silence = "sound:silence/1";

    public Task<string> MediaForAsync(string text, CancellationToken ct = default)
    {
        if (!isDevelopment)
        {
            throw new InvalidOperationException(
                "No speech synthesis is configured. Calls would ring customers and play " +
                "silence. Speech arrives in phase 22; until then the voice worker must not " +
                "run outside development.");
        }

        log.LogInformation("Would say: {Text}", text);

        return Task.FromResult(Silence);
    }
}
