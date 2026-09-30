using System.Buffers.Binary;

namespace MoynaPay.Application.Voice.Media.AudioSocket;

public static class AudioSocketProtocol
{
    public const byte TypeTerminate = 0x00;
    public const byte TypeUuid = 0x01;
    public const byte TypeAudio = 0x10;
    public const byte TypeError = 0xFF;

    public const int HeaderLength = 3;
    public const int UuidLength = 16;
    public const int MaxPayloadLength = ushort.MaxValue;
    public static readonly TimeSpan FrameDuration = TimeSpan.FromMilliseconds(20);
    public const int FramePayloadLength = 320;

    public static void WriteHeader(Span<byte> destination, byte type, int payloadLength)
    {
        if (destination.Length < HeaderLength)
            throw new ArgumentException("Destination is smaller than the AudioSocket header.", nameof(destination));
        if (payloadLength is < 0 or > MaxPayloadLength)
            throw new ArgumentOutOfRangeException(nameof(payloadLength),
                $"AudioSocket payloads must be 0..{MaxPayloadLength} bytes.");

        destination[0] = type;
        BinaryPrimitives.WriteUInt16BigEndian(destination[1..], (ushort)payloadLength);
    }

    public static (byte Type, int PayloadLength) ReadHeader(ReadOnlySpan<byte> header)
    {
        if (header.Length < HeaderLength)
            throw new ArgumentException("Header must be at least 3 bytes.", nameof(header));

        return (header[0], BinaryPrimitives.ReadUInt16BigEndian(header[1..]));
    }

    public static byte[] BuildMessage(byte type, ReadOnlySpan<byte> payload)
    {
        var message = new byte[HeaderLength + payload.Length];
        WriteHeader(message, type, payload.Length);
        payload.CopyTo(message.AsSpan(HeaderLength));
        return message;
    }
}
