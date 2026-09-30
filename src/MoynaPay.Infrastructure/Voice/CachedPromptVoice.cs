using System.Security.Cryptography;
using System.Text;
using MoynaPay.Application.Abstractions;
using MoynaPay.Application.Voice.Media;
using MoynaPay.Application.Voice.Speech;

namespace MoynaPay.Infrastructure.Voice;

public sealed class CachedPromptVoice(
    IStreamingSpeechSynthesizer synthesizer,
    TtsOptions options) : IPromptVoice
{
    public const string MediaPrefix = "sound:custom/";

    public async Task<string> MediaForAsync(string text, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);

        Directory.CreateDirectory(options.SoundsPath);

        var name = FileStem(text);
        var path = Path.Combine(options.SoundsPath, name + ".wav");

        if (!File.Exists(path))
        {
            var pcm = await SynthesizeAsync(text, ct).ConfigureAwait(false);
            var wav = WavPcm.Write(pcm, AudioFormat.Telephony8k);
            var temp = Path.Combine(options.SoundsPath, $".{name}.{Guid.CreateVersion7():N}.tmp");

            await File.WriteAllBytesAsync(temp, wav, ct).ConfigureAwait(false);
            TryMakeReadableByAsterisk(temp);

            if (File.Exists(path))
            {
                File.Delete(temp);
            }
            else
            {
                File.Move(temp, path);
            }
        }

        return MediaPrefix + name;
    }

    public string FileStem(string text)
    {
        var material = string.Join('\n',
            "moynapay-tts-v1",
            synthesizer.ProviderName,
            options.Model,
            options.Voice,
            AudioFormat.Telephony8k.SampleRateHz.ToString(),
            AudioFormat.Telephony8k.Channels.ToString(),
            text);

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(material));
        return "moynapay-" + Convert.ToHexString(hash)[..24].ToLowerInvariant();
    }

    private async Task<ReadOnlyMemory<byte>> SynthesizeAsync(string text, CancellationToken ct)
    {
        using var output = new MemoryStream();

        await foreach (var frame in synthesizer.SynthesizeAsync(text, ct).ConfigureAwait(false))
        {
            var converted = AudioResampler.Convert(frame, AudioFormat.Telephony8k);
            await output.WriteAsync(converted.Payload, ct).ConfigureAwait(false);
        }

        return output.ToArray();
    }

    private static void TryMakeReadableByAsterisk(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            File.SetUnixFileMode(path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite
                | UnixFileMode.GroupRead
                | UnixFileMode.OtherRead);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
        catch (PlatformNotSupportedException)
        {
        }
    }
}
