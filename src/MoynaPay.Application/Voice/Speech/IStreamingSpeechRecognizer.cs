using MoynaPay.Application.Voice.Media;

namespace MoynaPay.Application.Voice.Speech;

public sealed record Transcript(string Text, bool IsFinal, double? Confidence, DateTimeOffset At);

/// <summary>
/// Streaming speech to text. Empty final transcripts are normal; silence is not an error.
/// </summary>
public interface IStreamingSpeechRecognizer : IAsyncDisposable
{
    string ProviderName { get; }

    AudioFormat RequiredFormat { get; }

    IAsyncEnumerable<Transcript> TranscribeAsync(
        IAsyncEnumerable<AudioFrame> audio,
        CancellationToken ct);
}
