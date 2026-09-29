using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.Extensions.Configuration;
using MoynaPay.Application.Voice.Ari;

namespace MoynaPay.Infrastructure.Voice;

public enum AriConnectionState
{
    Disconnected,
    Connected,
}

/// <summary>
/// The ARI event socket, behind an interface so the reconnect and dispatch logic can be
/// exercised without an Asterisk. Everything interesting about the stream - backoff, dropped
/// connections, a malformed frame, cancellation - is untestable otherwise, and those are
/// exactly the paths that only run on a bad day.
/// </summary>
public interface IAriSocket : IAsyncDisposable
{
    ValueTask ConnectAsync(Uri uri, CancellationToken ct);

    /// <summary>One text message, or null when the peer closed the connection.</summary>
    ValueTask<string?> ReceiveAsync(CancellationToken ct);
}

public interface IAriSocketFactory
{
    IAriSocket Create();
}

/// <summary>
/// The ARI event socket, reconnecting for as long as it is asked to run.
///
/// Two rules, both worth stating because both are easy to break later:
///
///   * The read loop does nothing but read and yield. Anything slow belongs to the consumer,
///     on its own task. A handler that blocks here stops every event for every call - one
///     shop's slow webhook would silence another shop's customer mid-sentence.
///   * A dropped connection is normal. The stream reconnects with backoff and says so,
///     rather than throwing at whoever happened to be enumerating it.
/// </summary>
public sealed class AriEventStream
{
    private readonly IAriSocketFactory _sockets;
    private readonly string _baseUrl;
    private readonly string _app;
    private readonly string _username;
    private readonly string _password;

    public AriEventStream(IConfiguration configuration, IAriSocketFactory? sockets = null)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        _sockets = sockets ?? new ClientWebSocketAriSocketFactory();
        _baseUrl = configuration["Telephony:AriBaseUrl"] ?? "http://asterisk:8088/ari";
        _app = configuration["Telephony:StasisAppName"] ?? "moynapay";
        _username = configuration["Telephony:AriUsername"] ?? "moynapay";
        _password = configuration["Telephony:AriPassword"] ?? "moynapay_dev_password";
    }

    /// <summary>Raised on every connect and disconnect, so a host can report health.</summary>
    public event Action<AriConnectionState>? ConnectionChanged;

    /// <summary>Frames that did not parse, reported rather than silently dropped.</summary>
    public event Action<string>? MalformedEvent;

    public Uri BuildUri()
    {
        var socketBase = _baseUrl
            .Replace("https://", "wss://", StringComparison.OrdinalIgnoreCase)
            .Replace("http://", "ws://", StringComparison.OrdinalIgnoreCase)
            .TrimEnd('/');

        // api_key rather than a Basic header: ClientWebSocket cannot set one before the
        // handshake on every platform, and ARI accepts the credential in the query string
        // for exactly this reason.
        return new Uri(
            $"{socketBase}/events?app={Uri.EscapeDataString(_app)}" +
            $"&subscribeAll=false" +
            $"&api_key={Uri.EscapeDataString(_username)}:{Uri.EscapeDataString(_password)}");
    }

    public async IAsyncEnumerable<AriEvent> ReadAsync([EnumeratorCancellation] CancellationToken ct)
    {
        var uri = BuildUri();
        var consecutiveFailures = 0;

        while (!ct.IsCancellationRequested)
        {
            var connectedAt = DateTimeOffset.UtcNow;
            var socket = _sockets.Create();
            var connected = false;

            try
            {
                await socket.ConnectAsync(uri, ct).ConfigureAwait(false);
                connected = true;
            }
            catch (OperationCanceledException)
            {
                await socket.DisposeAsync().ConfigureAwait(false);
                yield break;
            }
            catch (Exception)
            {
                // Asterisk not up yet, or credentials rejected. Either way: back off and try
                // again. Throwing here would end the worker over a restart.
            }

            if (connected)
            {
                consecutiveFailures = 0;
                ConnectionChanged?.Invoke(AriConnectionState.Connected);

                while (true)
                {
                    string? message;

                    try
                    {
                        message = await socket.ReceiveAsync(ct).ConfigureAwait(false);
                    }
                    catch (Exception)
                    {
                        message = null;
                    }

                    if (message is null) break;
                    if (message.Length == 0) continue;

                    var parsed = AriEvent.TryParse(message);

                    if (parsed is null)
                    {
                        MalformedEvent?.Invoke(message);
                        continue;
                    }

                    yield return parsed;
                }

                ConnectionChanged?.Invoke(AriConnectionState.Disconnected);
            }

            await socket.DisposeAsync().ConfigureAwait(false);

            if (ct.IsCancellationRequested) yield break;

            consecutiveFailures = AriReconnect.ResetsBackoff(
                connected, DateTimeOffset.UtcNow - connectedAt) ? 0 : consecutiveFailures + 1;

            try
            {
                await Task.Delay(AriReconnect.Delay(consecutiveFailures), ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                yield break;
            }
        }
    }
}

internal sealed class ClientWebSocketAriSocketFactory : IAriSocketFactory
{
    public IAriSocket Create() => new ClientWebSocketAriSocket();
}

internal sealed class ClientWebSocketAriSocket : IAriSocket
{
    private readonly ClientWebSocket _socket = new();

    /// <summary>
    /// One event can exceed a single WebSocket frame, so the frames of a message are joined
    /// until EndOfMessage. Treating each frame as an event produces JSON that does not parse
    /// and an event that is silently lost - once every few thousand calls, which is the
    /// hardest kind of bug to find.
    /// </summary>
    private readonly byte[] _buffer = new byte[16 * 1024];

    public async ValueTask ConnectAsync(Uri uri, CancellationToken ct)
    {
        _socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);

        await _socket.ConnectAsync(uri, ct).ConfigureAwait(false);
    }

    public async ValueTask<string?> ReceiveAsync(CancellationToken ct)
    {
        var message = new StringBuilder();

        while (true)
        {
            var result = await _socket.ReceiveAsync(_buffer, ct).ConfigureAwait(false);

            if (result.MessageType == WebSocketMessageType.Close) return null;

            message.Append(Encoding.UTF8.GetString(_buffer, 0, result.Count));

            if (result.EndOfMessage) return message.ToString();
        }
    }

    public ValueTask DisposeAsync()
    {
        _socket.Dispose();

        return ValueTask.CompletedTask;
    }
}
