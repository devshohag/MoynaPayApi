namespace MoynaPay.Application.Voice.Ai;

// Section 16 - AI & Quality Intelligence.
// "AI is an enhancement layer, not a dependency for basic call-center operation. The product
//  must remain fully usable if AI services are disabled or temporarily unavailable."
// Provider abstraction box: "Use ISpeechToTextProvider, ISummaryProvider, IQaScoringProvider
// and similar interfaces so the platform can change between cloud providers or self-hosted
// models without changing core domain logic."

public record TranscriptResult(string Text, double? ConfidenceScore);
public record SpeechSynthesisResult(byte[] Audio, string ContentType, int DurationMilliseconds);
public record SummaryResult(string Summary, string? SuggestedDisposition, string? SuggestedFollowUp);
public record HandoffContextResult(
    string Summary,
    string CustomerIntent,
    string DetectedLanguage,
    string Sentiment,
    IReadOnlyList<string> CollectedDetails,
    IReadOnlyList<string> UnresolvedItems,
    string SuggestedOpening,
    string Provider,
    bool UsedFallback);
public record QaScoreResult(int Score, IReadOnlyDictionary<string, string> FormAnswers);
public record SentimentResult(string Label, double Score); // e.g. "positive" | "neutral" | "negative"

public interface ISpeechToTextProvider
{
    Task<TranscriptResult> TranscribeAsync(string recordingObjectStorageKey, CancellationToken ct = default);
}

public interface ITextToSpeechProvider
{
    Task<SpeechSynthesisResult> SynthesizeAsync(string text, string language = "en-US", CancellationToken ct = default);
}

public interface IProviderUsageWriter
{
    Task RecordAsync(Guid tenantId, string provider, string operation, decimal units,
        string unitType, long costMicros, int durationMilliseconds, CancellationToken ct = default);
}

public interface ISummaryProvider
{
    Task<SummaryResult> SummarizeAsync(string transcriptText, CancellationToken ct = default);
}

/// <summary>
/// Creates the bounded, structured context shown to a human agent before a live transfer.
/// TenantId is explicit because provider profiles and data-isolation rules are tenant scoped.
/// Implementations must return a safe fallback rather than blocking the telephone transfer.
/// </summary>
public interface IHandoffContextSummarizer
{
    Task<HandoffContextResult> SummarizeAsync(Guid tenantId, string transcriptText,
        string handoffReason, CancellationToken ct = default);
}

public interface IQaScoringProvider
{
    Task<QaScoreResult> ScoreAsync(string transcriptText, string qaFormDefinitionJson, CancellationToken ct = default);
}

public interface ISentimentProvider
{
    Task<SentimentResult> AnalyzeAsync(string text, CancellationToken ct = default);
}

/// <summary>
/// "Redaction/masking pipeline before sending sensitive transcript content to external AI
/// providers where required" (Section 16). Run every transcript through this before it
/// leaves the process boundary to a third-party AI provider.
/// </summary>
public interface ITranscriptRedactor
{
    string Redact(string rawText);
}

public interface IAiToolHandler
{
    string Name { get; }
    Task<string> ExecuteAsync(Guid tenantId, string argumentsJson, CancellationToken ct = default);
}
