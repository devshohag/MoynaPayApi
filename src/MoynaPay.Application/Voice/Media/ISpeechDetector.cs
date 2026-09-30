namespace MoynaPay.Application.Voice.Media;

public readonly record struct SpeechProbability(double Value, TimeSpan Duration);

public interface ISpeechDetector : IDisposable
{
    int RequiredSampleRateHz { get; }

    int WindowSamples { get; }

    IReadOnlyList<SpeechProbability> Analyze(AudioFrame frame);

    void Reset();
}
