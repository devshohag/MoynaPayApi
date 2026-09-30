using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Threading.Channels;

namespace MoynaPay.Application.Voice.Media.AudioSocket;

public sealed class AudioSocketTransport : IAudioTransport
{
    private readonly TcpClient _client;
    private readonly NetworkStream _stream;
    private readonly AudioSocketOptions _options;
    private readonly CancellationTokenSource _closed = new();
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly Channel<byte[]> _output;
    private readonly Task _pump;

    private long _receivedSequence;
    private int _disposed;

    internal AudioSocketTransport(string transportId, TcpClient client, AudioSocketOptions options)
    {
        TransportId = transportId;
        _client = client;
        _stream = client.GetStream();
        _options = options;

        _output = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(options.OutputQueueFrames)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false,
        });

        _pump = Task.Run(PumpOutputAsync);
    }

    public string TransportId { get; }

    public AudioFormat Format => AudioFormat.Telephony8k;

    public long DroppedOutputFrames { get; private set; }

    public async IAsyncEnumerable<AudioFrame> ReceiveAsync(
        [EnumeratorCancellation] CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _closed.Token);
        var header = new byte[AudioSocketProtocol.HeaderLength];

        while (!linked.Token.IsCancellationRequested)
        {
            byte type;
            int length;
            try
            {
                await _stream.ReadExactlyAsync(header, linked.Token).ConfigureAwait(false);
                (type, length) = AudioSocketProtocol.ReadHeader(header);
            }
            catch (Exception ex) when (ex is EndOfStreamException or IOException
                                           or ObjectDisposedException or OperationCanceledException)
            {
                yield break;
            }

            byte[]? payload = null;
            if (length > 0)
            {
                payload = new byte[length];
                try
                {
                    await _stream.ReadExactlyAsync(payload, linked.Token).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is EndOfStreamException or IOException
                                               or ObjectDisposedException or OperationCanceledException)
                {
                    yield break;
                }
            }

            switch (type)
            {
                case AudioSocketProtocol.TypeTerminate:
                    yield break;

                case AudioSocketProtocol.TypeAudio when payload is { Length: AudioSocketProtocol.FramePayloadLength }:
                    yield return new AudioFrame(payload, Format,
                        Interlocked.Increment(ref _receivedSequence), DateTimeOffset.UtcNow);
                    break;

                case AudioSocketProtocol.TypeError:
                    yield break;

                case AudioSocketProtocol.TypeUuid:
                default:
                    break;
            }
        }
    }

    public ValueTask SendAsync(AudioFrame frame, CancellationToken ct)
    {
        if (Volatile.Read(ref _disposed) != 0 || _closed.IsCancellationRequested)
            return ValueTask.CompletedTask;

        foreach (var chunk in Split(frame.Payload))
        {
            if (!_output.Writer.TryWrite(chunk))
                DroppedOutputFrames++;
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask FlushOutputAsync(CancellationToken ct)
    {
        while (_output.Reader.TryRead(out _))
        {
        }

        return ValueTask.CompletedTask;
    }

    public async ValueTask TerminateAsync(CancellationToken ct)
    {
        if (_closed.IsCancellationRequested)
            return;

        await FlushOutputAsync(ct).ConfigureAwait(false);

        try
        {
            await _writeLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                await _stream.WriteAsync(
                    AudioSocketProtocol.BuildMessage(AudioSocketProtocol.TypeTerminate, []), ct)
                    .ConfigureAwait(false);
                await _stream.FlushAsync(ct).ConfigureAwait(false);
            }
            finally
            {
                _writeLock.Release();
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException)
        {
        }

        await _closed.CancelAsync().ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        _output.Writer.TryComplete();
        if (!_closed.IsCancellationRequested)
            await _closed.CancelAsync().ConfigureAwait(false);

        try
        {
            await _pump.ConfigureAwait(false);
        }
        catch
        {
        }

        _closed.Dispose();
        _writeLock.Dispose();
        _stream.Dispose();
        _client.Dispose();
    }

    private static IEnumerable<byte[]> Split(ReadOnlyMemory<byte> payload)
    {
        const int size = AudioSocketProtocol.FramePayloadLength;

        for (var offset = 0; offset < payload.Length; offset += size)
        {
            var take = Math.Min(size, payload.Length - offset);
            yield return payload.Slice(offset, take).ToArray();
        }
    }

    private async Task PumpOutputAsync()
    {
        using var timer = new PeriodicTimer(AudioSocketProtocol.FrameDuration);

        try
        {
            while (await _output.Reader.WaitToReadAsync(_closed.Token).ConfigureAwait(false))
            {
                while (_output.Reader.TryRead(out var chunk))
                {
                    await timer.WaitForNextTickAsync(_closed.Token).ConfigureAwait(false);

                    await _writeLock.WaitAsync(_closed.Token).ConfigureAwait(false);
                    try
                    {
                        await _stream.WriteAsync(
                            AudioSocketProtocol.BuildMessage(AudioSocketProtocol.TypeAudio, chunk),
                            _closed.Token).ConfigureAwait(false);
                    }
                    finally
                    {
                        _writeLock.Release();
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
        }
    }
}
