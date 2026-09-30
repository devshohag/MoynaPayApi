using Microsoft.Extensions.Configuration;
using MoynaPay.Application.Voice.Ari;
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

    /// <summary>The channel variable that names the call, read back when a channel arrives
    /// without a registration.</summary>
    public const string SessionVariable = "MOYNAPAY_CALL_SESSION_ID";

    /// <summary>
    /// Places a call on a channel id WE choose.
    ///
    /// This is the outbound correlation fix, and it is two changes to one request.
    ///
    /// The channel id is supplied rather than read from the response. ARI accepts one on
    /// POST /channels, and taking it means the id is known before Asterisk creates the
    /// channel - so a StasisStart that arrives before the HTTP response comes back (which
    /// happens on a busy trunk, and only on a busy trunk) is still recognised. Reading the
    /// id from the response and correlating on it afterwards is a race that passes every
    /// test on a quiet system.
    ///
    /// The variables go in the JSON body rather than the query string. Asterisk's own
    /// documentation puts them there; the query-string form is accepted by some builds and
    /// quietly ignored by others, and a variable that is quietly ignored is a fallback that
    /// is not there on the day the first change is needed.
    /// </summary>
    public async Task OriginateAsync(Guid callSessionId, string channelId, string fromNumber,
        string toNumber, string trunkName, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(channelId);

        var endpoint = $"PJSIP/{toNumber}@{trunkName}";

        await OriginateEndpointAsync(callSessionId, channelId, fromNumber, endpoint, ct)
            .ConfigureAwait(false);
    }

    public async Task OriginateEndpointAsync(Guid callSessionId, string channelId, string fromNumber,
        string endpoint, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(channelId);
        ArgumentException.ThrowIfNullOrWhiteSpace(endpoint);

        var query = $"endpoint={Escape(endpoint)}&app={Escape(_appName)}" +
                    $"&callerId={Escape(fromNumber)}&channelId={Escape(channelId)}";

        var body = JsonSerializer.Serialize(new
        {
            variables = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [SessionVariable] = callSessionId.ToString(),
            },
        });

        using var content = new StringContent(body, Encoding.UTF8, "application/json");

        using var response = await SendWithRetryAsync(
            () => _httpClient.PostAsync($"channels?{query}", content, ct), ct);

        await EnsureSuccessAsync(response, ct);
    }

    /// <summary>
    /// Reads a channel variable. The fallback half of correlation: an Asterisk that ignored
    /// the supplied channel id would otherwise leave a call nothing can steer, and a call
    /// nothing can steer is a customer listening to silence.
    /// </summary>
    public async Task<string?> GetVariableAsync(string channelId, string variable,
        CancellationToken ct = default)
    {
        using var response = await SendWithRetryAsync(() => _httpClient.GetAsync(
            $"channels/{Escape(channelId)}/variable?variable={Escape(variable)}", ct), ct);

        // A channel that has gone, or a variable that was never set. Neither is a fault:
        // this is the path for a channel we may not own at all.
        if (response.StatusCode is System.Net.HttpStatusCode.NotFound
            or System.Net.HttpStatusCode.Conflict)
        {
            return null;
        }

        await EnsureSuccessAsync(response, ct);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));

        return document.RootElement.TryGetProperty("value", out var value)
            ? value.GetString()
            : null;
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

    public async Task<string> StartExternalMediaAsync(
        Guid callSessionId,
        string channelId,
        ExternalMediaOptions options,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(channelId);
        ArgumentNullException.ThrowIfNull(options);

        var externalChannelId = $"media-{callSessionId:N}";
        var query =
            $"app={Escape(_appName)}" +
            $"&channelId={Escape(externalChannelId)}" +
            $"&external_host={Escape(options.ExternalHost)}" +
            $"&format={Escape(options.Format)}" +
            $"&encapsulation={Escape(options.Encapsulation)}" +
            $"&transport={Escape(options.Transport)}" +
            $"&connection_type={Escape(options.ConnectionType)}";

        var body = JsonSerializer.Serialize(new
        {
            variables = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [SessionVariable] = callSessionId.ToString(),
                ["MOYNAPAY_CONTROL_CHANNEL_ID"] = channelId,
            },
        });

        using var content = new StringContent(body, Encoding.UTF8, "application/json");
        using var response = await SendWithRetryAsync(
            () => _httpClient.PostAsync($"channels/externalMedia?{query}", content, ct), ct);

        await EnsureSuccessAsync(response, ct);
        return externalChannelId;
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
