namespace MoynaPay.Application.Voice.Media;

public sealed class MediaDiagnostics
{
    private long _framesReceived;
    private long _framesSent;
    private long _inputFramesDropped;
    private long _outputFramesDropped;
    private long _resampleOperations;
    private int _inputQueueDepth;
    private int _peakInputQueueDepth;

    public long FramesReceived => Interlocked.Read(ref _framesReceived);
    public long FramesSent => Interlocked.Read(ref _framesSent);
    public long InputFramesDropped => Interlocked.Read(ref _inputFramesDropped);
    public long OutputFramesDropped => Interlocked.Read(ref _outputFramesDropped);
    public long ResampleOperations => Interlocked.Read(ref _resampleOperations);
    public int InputQueueDepth => Volatile.Read(ref _inputQueueDepth);
    public int PeakInputQueueDepth => Volatile.Read(ref _peakInputQueueDepth);

    internal void FrameReceived() => Interlocked.Increment(ref _framesReceived);
    internal void FrameSent() => Interlocked.Increment(ref _framesSent);
    internal void InputDropped() => Interlocked.Increment(ref _inputFramesDropped);
    internal void OutputDropped() => Interlocked.Increment(ref _outputFramesDropped);
    internal void Resampled() => Interlocked.Increment(ref _resampleOperations);

    internal void QueueDepthChanged(int delta)
    {
        var depth = Interlocked.Add(ref _inputQueueDepth, delta);

        int peak;
        while (depth > (peak = Volatile.Read(ref _peakInputQueueDepth)))
        {
            if (Interlocked.CompareExchange(ref _peakInputQueueDepth, depth, peak) == peak)
                break;
        }
    }
}
