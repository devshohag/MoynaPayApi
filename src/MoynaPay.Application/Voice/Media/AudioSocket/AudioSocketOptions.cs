using System.Net;

namespace MoynaPay.Application.Voice.Media.AudioSocket;

public sealed class AudioSocketOptions
{
    public IPAddress Address { get; set; } = IPAddress.Any;

    public int Port { get; set; } = 9092;

    public int OutputQueueFrames { get; set; } = 500;

    public TimeSpan HandshakeTimeout { get; set; } = TimeSpan.FromSeconds(5);
}
