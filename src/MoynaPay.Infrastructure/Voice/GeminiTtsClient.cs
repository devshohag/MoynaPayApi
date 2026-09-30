using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Text.Json;
using MoynaPay.Application.Voice.Media;
using MoynaPay.Application.Voice.Speech;

namespace MoynaPay.Infrastructure.Voice;

public sealed class GeminiTtsClient(HttpClient http, TtsOptions options) : IStreamingSpeechSynthesizer
{
    private const string GeminiBaseUrl = "https://generativelanguage.googleapis.com/v1beta/models/";

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

        using var request = new HttpRequestMessage(HttpMethod.Post, BuildUri(options.Model));
        request.Headers.Add("x-goog-api-key", options.ApiKey);
        request.Content = JsonContent.Create(BuildRequest(text, options.Voice));

        using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            throw new HttpRequestException(
                $"Gemini TTS returned {(int)response.StatusCode} ({response.ReasonPhrase}): {error}");
        }

        await using var body = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var json = await JsonDocument.ParseAsync(body, cancellationToken: ct).ConfigureAwait(false);

        var (pcm, format) = ExtractAudio(json.RootElement);

        yield return new AudioFrame(
            pcm,
            format,
            0,
            DateTimeOffset.UtcNow);
    }

    public static Uri BuildUri(string model)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(model);

        return new Uri(GeminiBaseUrl + Uri.EscapeDataString(model) + ":generateContent");
    }

    public static object BuildRequest(string text, string voice) => new
    {
        contents = new[]
        {
            new
            {
                role = "user",
                parts = new[]
                {
                    new { text },
                },
            },
        },
        generationConfig = new
        {
            responseModalities = new[] { "AUDIO" },
            speechConfig = new
            {
                voiceConfig = new
                {
                    prebuiltVoiceConfig = new
                    {
                        voiceName = voice,
                    },
                },
            },
        },
    };

    public static (byte[] Pcm, AudioFormat Format) ExtractAudio(JsonElement root)
    {
        if (TryFindAudioData(root, out var audio))
        {
            var bytes = Convert.FromBase64String(audio.Base64);
            return DecodeAudio(bytes, audio.MimeType);
        }

        throw new InvalidOperationException("Gemini returned no audio data.");
    }

    private static (byte[] Pcm, AudioFormat Format) DecodeAudio(byte[] bytes, string? mimeType)
    {
        if (mimeType?.StartsWith("audio/wav", StringComparison.OrdinalIgnoreCase) == true)
        {
            var (pcm, format) = WavPcm.Read(bytes);
            return (pcm.ToArray(), format);
        }

        return (bytes, FormatFromMimeType(mimeType));
    }

    private static AudioFormat FormatFromMimeType(string? mimeType)
    {
        if (string.IsNullOrWhiteSpace(mimeType)
            || !mimeType.StartsWith("audio/l16", StringComparison.OrdinalIgnoreCase))
        {
            return AudioFormat.Gemini24k;
        }

        var rate = 24000;
        var match = Regex.Match(mimeType, @"(?:rate|sample_rate)\s*=\s*(\d+)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (match.Success && int.TryParse(match.Groups[1].Value, out var parsed))
        {
            rate = parsed;
        }

        return new AudioFormat(AudioEncoding.Slin16, rate, 1);
    }

    private static bool TryFindAudioData(JsonElement element, out (string Base64, string? MimeType) audio)
    {
        audio = default;

        if (element.ValueKind == JsonValueKind.Object)
        {
            if (TryReadAudioObject(element, out audio))
            {
                return true;
            }

            foreach (var property in element.EnumerateObject())
            {
                if (TryFindAudioData(property.Value, out audio))
                {
                    return true;
                }
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                if (TryFindAudioData(item, out audio))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool TryReadAudioObject(JsonElement element, out (string Base64, string? MimeType) audio)
    {
        audio = default;

        var container = element;
        if (element.TryGetProperty("inlineData", out var inlineData))
        {
            container = inlineData;
        }
        else if (element.TryGetProperty("inline_data", out var inlineDataSnake))
        {
            container = inlineDataSnake;
        }

        if (!LooksLikeAudioObject(container)
            || !container.TryGetProperty("data", out var data)
            || data.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        var base64 = data.GetString() ?? "";
        if (string.IsNullOrWhiteSpace(base64))
        {
            return false;
        }

        audio = (base64, ReadMimeType(container));
        return true;
    }

    private static string? ReadMimeType(JsonElement element)
    {
        if (element.TryGetProperty("mime_type", out var snake))
        {
            return snake.GetString();
        }

        return element.TryGetProperty("mimeType", out var camel) ? camel.GetString() : null;
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
