using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using MoynaPay.Application.Voice.Media;
using MoynaPay.Application.Voice.Speech;

namespace MoynaPay.Infrastructure.Voice;

public sealed class GeminiTtsClient(HttpClient http, TtsOptions options) : IStreamingSpeechSynthesizer
{
    private static readonly Uri Interactions = new("https://generativelanguage.googleapis.com/v1beta/interactions");

    public string ProviderName => "gemini";

    public AudioFormat OutputFormat => AudioFormat.Gemini24k;

    public async IAsyncEnumerable<AudioFrame> SynthesizeAsync(
        string text,
        [EnumeratorCancellation] CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);

        if (!options.CanSynthesize)
        {
            throw new InvalidOperationException(
                "Text to speech is not configured. Set MoynaPay:Gemini:ApiKey, " +
                "Telephony:Tts:Model, and Telephony:Tts:Voice.");
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, Interactions);
        request.Headers.Add("x-goog-api-key", options.ApiKey);
        request.Content = JsonContent.Create(new
        {
            model = options.Model,
            input = new[]
            {
                new
                {
                    type = "user_input",
                    content = new[]
                    {
                        new
                        {
                            type = "text",
                            text,
                        },
                    },
                },
            },
            response_format = new
            {
                type = "audio",
                mime_type = "audio/wav",
                sample_rate = 24000,
            },
            generation_config = new
            {
                speech_config = new[]
                {
                    new { voice = options.Voice },
                },
            },
        });

        using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        await using var body = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var json = await JsonDocument.ParseAsync(body, cancellationToken: ct).ConfigureAwait(false);

        var wav = ExtractAudio(json.RootElement);
        var (pcm, format) = WavPcm.Read(wav);

        yield return new AudioFrame(
            pcm.ToArray(),
            format,
            0,
            DateTimeOffset.UtcNow);
    }

    private static byte[] ExtractAudio(JsonElement root)
    {
        if (TryFindAudioData(root, out var base64))
        {
            return Convert.FromBase64String(base64);
        }

        throw new InvalidOperationException("Gemini returned no audio data.");
    }

    private static bool TryFindAudioData(JsonElement element, out string base64)
    {
        base64 = "";

        if (element.ValueKind == JsonValueKind.Object)
        {
            if (LooksLikeAudioObject(element)
                && element.TryGetProperty("data", out var data)
                && data.ValueKind == JsonValueKind.String)
            {
                base64 = data.GetString() ?? "";
                return !string.IsNullOrWhiteSpace(base64);
            }

            foreach (var property in element.EnumerateObject())
            {
                if (TryFindAudioData(property.Value, out base64))
                {
                    return true;
                }
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                if (TryFindAudioData(item, out base64))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool LooksLikeAudioObject(JsonElement element)
    {
        if (element.TryGetProperty("mime_type", out var mime)
            && mime.GetString()?.StartsWith("audio/", StringComparison.OrdinalIgnoreCase) == true)
        {
            return true;
        }

        if (element.TryGetProperty("mimeType", out var camel)
            && camel.GetString()?.StartsWith("audio/", StringComparison.OrdinalIgnoreCase) == true)
        {
            return true;
        }

        return element.TryGetProperty("type", out var type)
            && string.Equals(type.GetString(), "audio", StringComparison.OrdinalIgnoreCase);
    }
}
