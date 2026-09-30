namespace MoynaPay.Application.Voice.Ari;

public sealed class ExternalMediaOptions
{
    public string ExternalHost { get; set; } = "localhost:9092";

    public string Format { get; set; } = "slin";

    public string Encapsulation { get; set; } = "audiosocket";

    public string Transport { get; set; } = "tcp";

    public string ConnectionType { get; set; } = "client";

    public TimeSpan MediaTimeout { get; set; } = TimeSpan.FromSeconds(5);
}
