namespace MoynaPay.Application.Voice.Ai;

public sealed class DeterministicHandoffContextSummarizer : IHandoffContextSummarizer
{
    public Task<HandoffContextResult> SummarizeAsync(
        Guid tenantId,
        string transcriptText,
        string handoffReason,
        CancellationToken ct = default)
    {
        var transcript = Limit(transcriptText, 16_000);
        var reason = Limit(handoffReason, 500, "Human review requested.");
        var lastCustomerLine = LastCustomerLine(transcript);
        var summary = string.IsNullOrWhiteSpace(lastCustomerLine)
            ? reason
            : Limit(lastCustomerLine, 700);
        var language = DetectLanguage(transcript);

        return Task.FromResult(new HandoffContextResult(
            summary,
            reason,
            language,
            "unknown",
            [],
            ["Human agent should confirm the requested resolution."],
            Opening(language),
            "deterministic-fallback",
            true));
    }

    private static string LastCustomerLine(string transcript)
    {
        var lines = transcript.Split('\n',
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        return lines
            .Where(x => x.StartsWith("Customer:", StringComparison.OrdinalIgnoreCase))
            .Select(x => x["Customer:".Length..].Trim())
            .LastOrDefault(x => x.Length > 0)
            ?? transcript.Trim();
    }

    private static string DetectLanguage(string transcript) =>
        transcript.Any(x => x is >= '\u0980' and <= '\u09FF') ? "bn-BD" : "en-US";

    private static string Opening(string language) =>
        language == "bn-BD"
            ? "I have the previous conversation in front of me and can continue from here."
            : "I have your earlier conversation in front of me, so I can continue from here.";

    private static string Limit(string? value, int max, string fallback = "") =>
        string.IsNullOrWhiteSpace(value)
            ? fallback
            : value.Trim()[..Math.Min(value.Trim().Length, max)];
}
