using MoynaPay.Application.Voice.Media;

namespace MoynaPay.Application.Voice.Speech;

/// <summary>
/// Streaming text to speech. Implementations must yield the first chunk as early as
/// possible; total synthesis time matters far less than time to first chunk.
/// Cancelling the token must stop synthesis immediately.
/// </summary>
public interface IStreamingSpeechSynthesizer
{
    string ProviderName { get; }

    AudioFormat OutputFormat { get; }

    IAsyncEnumerable<AudioFrame> SynthesizeAsync(string text, CancellationToken ct);
}
