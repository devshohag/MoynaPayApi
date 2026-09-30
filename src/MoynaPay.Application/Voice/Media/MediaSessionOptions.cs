namespace MoynaPay.Application.Voice.Media;

public sealed class MediaSessionOptions
{
    public AudioFormat InputFormat { get; set; } = AudioFormat.Telephony8k;

    public int InputQueueFrames { get; set; } = 100;
}
