namespace MoynaPay.Application.Voice.Media;

public interface IMediaChannel : IPlaybackControl
{
    AudioFormat InputFormat { get; }

    AudioFormat OutputFormat { get; }

    IAsyncEnumerable<AudioFrame> ReceiveAsync(CancellationToken ct);

    ValueTask SendAsync(AudioFrame frame, CancellationToken ct);
}
