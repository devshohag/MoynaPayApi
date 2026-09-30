namespace MoynaPay.Application.Voice.Media;

public interface IAudioTransport : IAsyncDisposable
{
    string TransportId { get; }

    AudioFormat Format { get; }

    IAsyncEnumerable<AudioFrame> ReceiveAsync(CancellationToken ct);

    ValueTask SendAsync(AudioFrame frame, CancellationToken ct);

    ValueTask FlushOutputAsync(CancellationToken ct);

    ValueTask TerminateAsync(CancellationToken ct);
}
