namespace MoynaPay.Application.Voice.Media;

public interface IPlaybackControl
{
    ValueTask FlushOutputAsync(CancellationToken ct);
}
