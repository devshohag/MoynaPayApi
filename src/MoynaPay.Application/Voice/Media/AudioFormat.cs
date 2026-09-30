namespace MoynaPay.Application.Voice.Media;

public enum AudioEncoding
{
    /// <summary>Signed linear 16-bit PCM, little-endian.</summary>
    Slin16,
}

/// <summary>Describes the shape of an audio stream. Every frame carries one of these.</summary>
public readonly record struct AudioFormat(AudioEncoding Encoding, int SampleRateHz, int Channels)
{
    /// <summary>8 kHz mono - what Asterisk plays on the call.</summary>
    public static readonly AudioFormat Telephony8k = new(AudioEncoding.Slin16, 8000, 1);

    /// <summary>16 kHz mono - what many speech recognizers want.</summary>
    public static readonly AudioFormat Wide16k = new(AudioEncoding.Slin16, 16000, 1);

    /// <summary>24 kHz mono - what Gemini TTS returns by default.</summary>
    public static readonly AudioFormat Gemini24k = new(AudioEncoding.Slin16, 24000, 1);

    public int BytesPerSample => Encoding switch
    {
        AudioEncoding.Slin16 => 2,
        _ => throw new NotSupportedException($"Unknown encoding {Encoding}."),
    };

    public int BytesPerFrame(TimeSpan duration) =>
        (int)(SampleRateHz * duration.TotalSeconds) * BytesPerSample * Channels;

    public TimeSpan DurationOf(int byteCount) =>
        TimeSpan.FromSeconds((double)byteCount / (SampleRateHz * BytesPerSample * Channels));
}
