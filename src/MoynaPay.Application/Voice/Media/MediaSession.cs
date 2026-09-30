using System.Runtime.CompilerServices;
using System.Threading.Channels;

namespace MoynaPay.Application.Voice.Media;

public sealed class MediaSession : IMediaChannel, IAsyncDisposable
{
    private readonly IAudioTransport _transport;
    private readonly MediaSessionOptions _options;
    private readonly Channel<AudioFrame> _input;
    private readonly CancellationTokenSource _stopped = new();
    private readonly Task _reader;
    private int _disposed;

    public MediaSession(IAudioTransport transport, MediaSessionOptions? options = null)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _options = options ?? new MediaSessionOptions();

        _input = Channel.CreateBounded<AudioFrame>(new BoundedChannelOptions(_options.InputQueueFrames)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = true,
        });

        _reader = Task.Run(ReadTransportAsync);
    }

    public MediaDiagnostics Diagnostics { get; } = new();

    public AudioFormat InputFormat => _options.InputFormat;

    public AudioFormat TransportFormat => _transport.Format;

    public AudioFormat OutputFormat => _transport.Format;

    public async IAsyncEnumerable<AudioFrame> ReceiveAsync(
        [EnumeratorCancellation] CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _stopped.Token);

        while (true)
        {
            AudioFrame frame;
            try
            {
                if (!await _input.Reader.WaitToReadAsync(linked.Token).ConfigureAwait(false))
                    yield break;

                if (!_input.Reader.TryRead(out frame))
                    continue;
            }
            catch (OperationCanceledException)
            {
                yield break;
            }
            catch (ChannelClosedException)
            {
                yield break;
            }

            Diagnostics.QueueDepthChanged(-1);
            yield return frame;
        }
    }

    public async ValueTask SendAsync(AudioFrame frame, CancellationToken ct)
    {
        var converted = AudioResampler.Convert(frame, _transport.Format);
        if (converted.Format.SampleRateHz != frame.Format.SampleRateHz)
            Diagnostics.Resampled();

        await _transport.SendAsync(converted, ct).ConfigureAwait(false);
        Diagnostics.FrameSent();
    }

    public ValueTask FlushOutputAsync(CancellationToken ct) => _transport.FlushOutputAsync(ct);

    public ValueTask TerminateAsync(CancellationToken ct) => _transport.TerminateAsync(ct);

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        await _stopped.CancelAsync().ConfigureAwait(false);

        try
        {
            await _reader.ConfigureAwait(false);
        }
        catch
        {
        }

        _stopped.Dispose();
        await _transport.DisposeAsync().ConfigureAwait(false);
    }

    private async Task ReadTransportAsync()
    {
        try
        {
            await foreach (var frame in _transport.ReceiveAsync(_stopped.Token).ConfigureAwait(false))
            {
                Diagnostics.FrameReceived();

                var owned = frame.CopyPayload();
                var converted = AudioResampler.Convert(owned, _options.InputFormat);
                if (converted.Format.SampleRateHz != owned.Format.SampleRateHz)
                    Diagnostics.Resampled();

                if (_input.Reader.Count >= _options.InputQueueFrames)
                {
                    Diagnostics.InputDropped();
                    Diagnostics.QueueDepthChanged(-1);
                }

                if (_input.Writer.TryWrite(converted))
                    Diagnostics.QueueDepthChanged(1);
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            _input.Writer.TryComplete();
        }
    }
}
