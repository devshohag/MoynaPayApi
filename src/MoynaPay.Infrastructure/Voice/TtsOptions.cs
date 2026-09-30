using Microsoft.Extensions.Configuration;

namespace MoynaPay.Infrastructure.Voice;

public sealed record TtsOptions(
    string ApiKey,
    string Model,
    string Voice,
    string SoundsPath)
{
    public const string DefaultSoundsPath = "/var/lib/asterisk/sounds/custom";

    public static TtsOptions FromConfiguration(IConfiguration configuration, bool isDevelopment)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var apiKey = configuration["MoynaPay:Gemini:ApiKey"];
        var model = configuration["Telephony:Tts:Model"];
        var voice = configuration["Telephony:Tts:Voice"];
        var soundsPath = configuration["Telephony:SoundsPath"];

        if (string.IsNullOrWhiteSpace(soundsPath))
        {
            soundsPath = DefaultSoundsPath;
        }

        if (string.IsNullOrWhiteSpace(apiKey)
            || string.IsNullOrWhiteSpace(model)
            || string.IsNullOrWhiteSpace(voice))
        {
            if (!isDevelopment)
            {
                throw new InvalidOperationException(
                    "Text to speech is not configured. Set MoynaPay:Gemini:ApiKey, " +
                    "Telephony:Tts:Model, and Telephony:Tts:Voice. There is no default " +
                    "outside development: a worker that rings customers and plays silence " +
                    "is worse than one that refuses to start.");
            }
        }

        return new TtsOptions(
            apiKey?.Trim() ?? "",
            model?.Trim() ?? "",
            voice?.Trim() ?? "",
            soundsPath.Trim());
    }

    public bool CanSynthesize =>
        !string.IsNullOrWhiteSpace(ApiKey)
        && !string.IsNullOrWhiteSpace(Model)
        && !string.IsNullOrWhiteSpace(Voice);
}
