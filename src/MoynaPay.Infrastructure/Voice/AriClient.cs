using Microsoft.Extensions.Configuration;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace MoynaPay.Infrastructure.Voice;

/// <summary>Small authenticated wrapper around the ARI operations owned by this worker.</summary>
public sealed class AriClient
{
    private readonly HttpClient _httpClient;
    private readonly string _appName;

    public AriClient(IHttpClientFactory httpClientFactory, IConfiguration configuration)
    {
        _httpClient = httpClientFactory.CreateClient("ari");
        var baseUrl = configuration["Telephony:AriBaseUrl"] ?? "http://asterisk:8088/ari";
        _httpClient.BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/");
        var username = configuration["Telephony:AriUsername"] ?? "moynapay";
        var password = configuration["Telephony:AriPassword"] ?? "moynapay_dev_password";
        var basicAuth = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{username}:{password}"));
        _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", basicAuth);
        _appName = configuration["Telephony:StasisAppName"] ?? "moynapay";
    }

    public async Task<string?> OriginateAsync(Guid callSessionId, string fromNumber,
        string toNumber, string trunkName, CancellationToken ct = default)
    {
        var endpoint = $"PJSIP/{toNumber}@{trunkName}";
        var query = $"endpoint={Escape(endpoint)}&app={Escape(_appName)}&callerId={Escape(fromNumber)}" +
                    $"&variables[MOYNAPAY_CALL_SESSION_ID]={callSessionId}";
        using var response = await SendWithRetryAsync(() => _httpClient.PostAsync($"channels?{query}", null, ct), ct);
        await EnsureSuccessAsync(response, ct);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        return doc.RootElement.TryGetProperty("id", out var id) ? id.GetString() : null;
    }

    public async Task<HashSet<string>> ListChannelIdsAsync(CancellationToken ct = default)
    {
        using var response = await SendWithRetryAsync(() => _httpClient.GetAsync("channels", ct), ct);
        await EnsureSuccessAsync(response, ct);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        return document.RootElement.ValueKind == JsonValueKind.Array
            ? document.RootElement.EnumerateArray()
                .Select(x => x.TryGetProperty("id", out var id) ? id.GetString() : null)
                .Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x!).ToHashSet(StringComparer.Ordinal)
            : new HashSet<string>(StringComparer.Ordinal);
    }

    public async Task AnswerAsync(string channelId, CancellationToken ct = default)
    {
        using var response = await SendWithRetryAsync(() => _httpClient.PostAsync($"channels/{Escape(channelId)}/answer", null, ct), ct);
        await EnsureSuccessAsync(response, ct);
    }

    public async Task<string> PlayAsync(string channelId, string media, CancellationToken ct = default)
    {
        var playbackId = Guid.NewGuid().ToString("N");
        using var response = await SendWithRetryAsync(() => _httpClient.PostAsync(
            $"channels/{Escape(channelId)}/play/{playbackId}?media={Escape(media)}", null, ct), ct);
        await EnsureSuccessAsync(response, ct);
        return playbackId;
    }

    public async Task StartRecordingAsync(string channelId, string recordingName,
        int maxDurationSeconds, int maxSilenceSeconds, CancellationToken ct = default)
    {
        var query = $"name={Escape(recordingName)}&format=wav&maxDurationSeconds={maxDurationSeconds}" +
                    $"&maxSilenceSeconds={maxSilenceSeconds}&ifExists=overwrite&beep=true&terminateOn=%23";
        using var response = await SendWithRetryAsync(() => _httpClient.PostAsync(
            $"channels/{Escape(channelId)}/record?{query}", null, ct), ct);
        await EnsureSuccessAsync(response, ct);
    }

    public async Task HangupAsync(string channelId, CancellationToken ct = default)
    {
        using var response = await SendWithRetryAsync(() => _httpClient.DeleteAsync($"channels/{Escape(channelId)}", ct), ct);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return;
        await EnsureSuccessAsync(response, ct);
    }

    public async Task ContinueInDialplanAsync(string channelId, string context,
        string extension, int priority = 1, CancellationToken ct = default)
    {
        var query = $"context={Escape(context)}&extension={Escape(extension)}&priority={priority}";
        using var response = await SendWithRetryAsync(() => _httpClient.PostAsync(
            $"channels/{Escape(channelId)}/continue?{query}", null, ct), ct);
        await EnsureSuccessAsync(response, ct);
    }

    private static string Escape(string value) => Uri.EscapeDataString(value);

    private static async Task<HttpResponseMessage> SendWithRetryAsync(
        Func<Task<HttpResponseMessage>> send, CancellationToken ct)
    {
        Exception? lastError = null;
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            try
            {
                var response = await send();
                if ((int)response.StatusCode < 500 && response.StatusCode != System.Net.HttpStatusCode.RequestTimeout
                    && (int)response.StatusCode != 429) return response;
                if (attempt == 3) return response;
                response.Dispose();
            }
            catch (HttpRequestException ex) when (attempt < 3) { lastError = ex; }
            await Task.Delay(TimeSpan.FromMilliseconds(200 * Math.Pow(2, attempt - 1)), ct);
        }
        throw lastError ?? new HttpRequestException("Asterisk ARI request failed after retries.");
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode) return;
        var body = await response.Content.ReadAsStringAsync(ct);
        throw new HttpRequestException(
            $"Asterisk ARI returned {(int)response.StatusCode} ({response.ReasonPhrase}): {body}");
    }
}
