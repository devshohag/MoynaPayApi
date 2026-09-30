using System.Net;
using MoynaPay.Application.Abstractions;
using MoynaPay.Application.Voice;

namespace MoynaPay.Infrastructure.Voice;

public sealed class CircuitBreakerPromptVoice(
    CachedPromptVoice primary,
    FallbackPromptVoice fallback,
    SpeechQuotaCircuit circuit) : IPromptVoice
{
    public async Task<string> MediaForAsync(string text, CancellationToken ct = default)
    {
        if (circuit.IsOpen)
            return await fallback.MediaForAsync(text, ct).ConfigureAwait(false);

        try
        {
            return await primary.MediaForAsync(text, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException ex) when (IsQuotaFailure(ex))
        {
            circuit.Open();
            return await fallback.MediaForAsync(text, ct).ConfigureAwait(false);
        }
    }

    public static bool IsQuotaFailure(HttpRequestException ex)
    {
        if (ex.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.Forbidden)
            return true;

        return ex.Message.Contains("quota", StringComparison.OrdinalIgnoreCase)
            || ex.Message.Contains("rate limit", StringComparison.OrdinalIgnoreCase)
            || ex.Message.Contains("RESOURCE_EXHAUSTED", StringComparison.OrdinalIgnoreCase);
    }
}
