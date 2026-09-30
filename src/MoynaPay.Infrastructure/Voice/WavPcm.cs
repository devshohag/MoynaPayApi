using System.Buffers.Binary;
using System.Text;
using MoynaPay.Application.Voice.Media;

namespace MoynaPay.Infrastructure.Voice;

public static class WavPcm
{
    public static byte[] Write(ReadOnlyMemory<byte> pcm, AudioFormat format)
    {
        if (format.Encoding != AudioEncoding.Slin16)
        {
            throw new NotSupportedException("Only signed linear 16-bit PCM WAV is supported.");
        }

        var dataLength = pcm.Length;
        var output = new byte[44 + dataLength];
        var span = output.AsSpan();

        Encoding.ASCII.GetBytes("RIFF").CopyTo(span[..4]);
        BinaryPrimitives.WriteInt32LittleEndian(span[4..8], 36 + dataLength);
        Encoding.ASCII.GetBytes("WAVE").CopyTo(span[8..12]);
        Encoding.ASCII.GetBytes("fmt ").CopyTo(span[12..16]);
        BinaryPrimitives.WriteInt32LittleEndian(span[16..20], 16);
        BinaryPrimitives.WriteInt16LittleEndian(span[20..22], 1);
        BinaryPrimitives.WriteInt16LittleEndian(span[22..24], (short)format.Channels);
        BinaryPrimitives.WriteInt32LittleEndian(span[24..28], format.SampleRateHz);
        BinaryPrimitives.WriteInt32LittleEndian(span[28..32],
            format.SampleRateHz * format.Channels * format.BytesPerSample);
        BinaryPrimitives.WriteInt16LittleEndian(span[32..34],
            (short)(format.Channels * format.BytesPerSample));
        BinaryPrimitives.WriteInt16LittleEndian(span[34..36],
            (short)(format.BytesPerSample * 8));
        Encoding.ASCII.GetBytes("data").CopyTo(span[36..40]);
        BinaryPrimitives.WriteInt32LittleEndian(span[40..44], dataLength);
        pcm.Span.CopyTo(span[44..]);

        return output;
    }

    public static (ReadOnlyMemory<byte> Pcm, AudioFormat Format) Read(ReadOnlyMemory<byte> wav)
    {
        var span = wav.Span;
        if (span.Length < 44
            || !span[..4].SequenceEqual("RIFF"u8)
            || !span[8..12].SequenceEqual("WAVE"u8))
        {
            throw new InvalidOperationException("Gemini returned audio that is not a WAV file.");
        }

        var offset = 12;
        AudioFormat? format = null;
        Range? dataRange = null;

        while (offset + 8 <= span.Length)
        {
            var chunkId = span.Slice(offset, 4);
            var chunkLength = BinaryPrimitives.ReadInt32LittleEndian(span.Slice(offset + 4, 4));
            if (chunkLength < 0 || offset + 8 + chunkLength > span.Length)
            {
                throw new InvalidOperationException("Gemini returned a truncated WAV file.");
            }

            var chunk = span.Slice(offset + 8, chunkLength);

            if (chunkId.SequenceEqual("fmt "u8))
            {
                if (chunk.Length < 16)
                {
                    throw new InvalidOperationException("Gemini returned a WAV file without a valid format chunk.");
                }

                var audioFormat = BinaryPrimitives.ReadInt16LittleEndian(chunk[..2]);
                var channels = BinaryPrimitives.ReadInt16LittleEndian(chunk[2..4]);
                var sampleRate = BinaryPrimitives.ReadInt32LittleEndian(chunk[4..8]);
                var bitsPerSample = BinaryPrimitives.ReadInt16LittleEndian(chunk[14..16]);

                if (audioFormat != 1 || bitsPerSample != 16 || channels != 1)
                {
                    throw new NotSupportedException(
                        "Only 16-bit mono PCM WAV returned by Gemini is supported.");
                }

                format = new AudioFormat(AudioEncoding.Slin16, sampleRate, channels);
            }
            else if (chunkId.SequenceEqual("data"u8))
            {
                dataRange = new Range(offset + 8, offset + 8 + chunkLength);
            }

            offset += 8 + chunkLength + (chunkLength % 2);
        }

        if (format is not { } f || dataRange is not { } data)
        {
            throw new InvalidOperationException("Gemini returned a WAV file without audio data.");
        }

        return (wav[data], f);
    }
}
