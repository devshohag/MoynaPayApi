using System.Buffers.Binary;

namespace MoynaPay.Application.Voice.Media;

/// <summary>
/// Converts signed 16-bit PCM between sample rates. Gemini gives us 24 kHz; Asterisk wants
/// 8 kHz mono for confirmation calls.
/// </summary>
/// <remarks>
/// Upsampling is linear interpolation and downsampling averages the samples that collapse
/// into each output sample. That averaging is a crude low-pass: good enough for speech at
/// these rates, and deliberately simple. If a future task needs studio-quality resampling,
/// replace this class rather than complicating callers.
/// </remarks>
public static class AudioResampler
{
    /// <summary>
    /// Returns the payload converted to <paramref name="target"/>. When the formats already
    /// match, the input is returned unchanged and nothing is allocated.
    /// </summary>
    public static ReadOnlyMemory<byte> Convert(ReadOnlyMemory<byte> payload,
        AudioFormat source, AudioFormat target)
    {
        if (source.Encoding != AudioEncoding.Slin16 || target.Encoding != AudioEncoding.Slin16)
            throw new NotSupportedException("Only signed linear 16-bit PCM is supported.");
        if (source.Channels != 1 || target.Channels != 1)
            throw new NotSupportedException("Only mono audio is supported.");
        if (payload.Length % 2 != 0)
            throw new ArgumentException("A 16-bit PCM payload must have an even length.", nameof(payload));

        if (source.SampleRateHz == target.SampleRateHz)
            return payload;

        var input = ToSamples(payload.Span);
        var output = Resample(input, source.SampleRateHz, target.SampleRateHz);
        return ToBytes(output);
    }

    public static AudioFrame Convert(AudioFrame frame, AudioFormat target) =>
        frame.Format.SampleRateHz == target.SampleRateHz && frame.Format.Channels == target.Channels
            ? frame
            : frame with { Payload = Convert(frame.Payload, frame.Format, target), Format = target };

    private static short[] Resample(short[] input, int sourceRate, int targetRate)
    {
        if (input.Length == 0)
            return [];

        var outputLength = (int)Math.Round((double)input.Length * targetRate / sourceRate);
        if (outputLength <= 0)
            return [];

        var output = new short[outputLength];

        if (targetRate > sourceRate)
        {
            var step = (double)(input.Length - 1) / Math.Max(1, outputLength - 1);
            for (var i = 0; i < outputLength; i++)
            {
                var position = i * step;
                var index = (int)position;
                var fraction = position - index;

                var first = input[Math.Min(index, input.Length - 1)];
                var second = input[Math.Min(index + 1, input.Length - 1)];
                output[i] = (short)Math.Clamp(first + (second - first) * fraction,
                    short.MinValue, short.MaxValue);
            }
        }
        else
        {
            var ratio = (double)input.Length / outputLength;
            for (var i = 0; i < outputLength; i++)
            {
                var start = (int)(i * ratio);
                var end = Math.Min((int)((i + 1) * ratio), input.Length);
                if (end <= start) end = Math.Min(start + 1, input.Length);

                long sum = 0;
                for (var j = start; j < end; j++)
                    sum += input[j];

                output[i] = (short)Math.Clamp(sum / (end - start), short.MinValue, short.MaxValue);
            }
        }

        return output;
    }

    private static short[] ToSamples(ReadOnlySpan<byte> payload)
    {
        var samples = new short[payload.Length / 2];
        for (var i = 0; i < samples.Length; i++)
            samples[i] = BinaryPrimitives.ReadInt16LittleEndian(payload[(i * 2)..]);

        return samples;
    }

    private static byte[] ToBytes(short[] samples)
    {
        var payload = new byte[samples.Length * 2];
        for (var i = 0; i < samples.Length; i++)
            BinaryPrimitives.WriteInt16LittleEndian(payload.AsSpan(i * 2), samples[i]);

        return payload;
    }
}
