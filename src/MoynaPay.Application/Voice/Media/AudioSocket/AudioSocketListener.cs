using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;

namespace MoynaPay.Application.Voice.Media.AudioSocket;

public sealed class AudioSocketListener : IAsyncDisposable
{
    private readonly AudioSocketOptions _options;
    private readonly TcpListener _listener;
    private int _started;
    private int _disposed;

    public AudioSocketListener(AudioSocketOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _listener = new TcpListener(options.Address, options.Port);
    }

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    public void Start()
    {
        if (Interlocked.Exchange(ref _started, 1) != 0)
            return;

        _listener.Start();
    }

    public async IAsyncEnumerable<AudioSocketTransport> AcceptAsync(
        [EnumeratorCancellation] CancellationToken ct)
    {
        Start();

        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                yield break;
            }
            catch (ObjectDisposedException)
            {
                yield break;
            }
            catch (SocketException)
            {
                continue;
            }

            client.NoDelay = true;

            var transport = await HandshakeAsync(client, ct).ConfigureAwait(false);
            if (transport is null)
            {
                client.Dispose();
                continue;
            }

            yield return transport;
        }
    }

    private async Task<AudioSocketTransport?> HandshakeAsync(TcpClient client, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(_options.HandshakeTimeout);

        try
        {
            var stream = client.GetStream();
            var header = new byte[AudioSocketProtocol.HeaderLength];
            await stream.ReadExactlyAsync(header, timeout.Token).ConfigureAwait(false);

            var (type, length) = AudioSocketProtocol.ReadHeader(header);
            if (type != AudioSocketProtocol.TypeUuid || length != AudioSocketProtocol.UuidLength)
                return null;

            var payload = new byte[length];
            await stream.ReadExactlyAsync(payload, timeout.Token).ConfigureAwait(false);

            return new AudioSocketTransport(new Guid(payload).ToString("D"), client, _options);
        }
        catch (Exception ex) when (ex is EndOfStreamException or IOException
                                       or ObjectDisposedException or OperationCanceledException
                                       or ArgumentException)
        {
            return null;
        }
    }

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
            _listener.Dispose();

        return ValueTask.CompletedTask;
    }
}
