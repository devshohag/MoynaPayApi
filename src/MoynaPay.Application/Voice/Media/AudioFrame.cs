namespace MoynaPay.Application.Voice.Media;

/// <summary>
/// One block of PCM audio. The payload may be borrowed from a transport buffer; copy it if
/// it has to outlive the next frame.
/// </summary>
public readonly record struct AudioFrame(
    ReadOnlyMemory<byte> Payload,
    AudioFormat Format,
    long SequenceNumber,
    DateTimeOffset CapturedAt)
{
    public TimeSpan Duration => Format.DurationOf(Payload.Length);

    public AudioFrame CopyPayload() =>
        this with { Payload = Payload.ToArray() };
}
