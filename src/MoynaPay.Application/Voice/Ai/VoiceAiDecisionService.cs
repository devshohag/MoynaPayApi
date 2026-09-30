using System.Globalization;
using System.Text;
using System.Text.Json;
using MoynaPay.Application.Voice.Speech;
using MoynaPay.Application.Workflows;
using MoynaPay.Domain.Orders;

namespace MoynaPay.Application.Voice.Ai;

public sealed record VoiceAiDecisionCommand
{
    public required Guid MerchantId { get; init; }
    public required Guid OrderId { get; init; }
    public required Guid CallSessionId { get; init; }
    public required IReadOnlyList<Transcript> Transcript { get; init; }
    public double ConfidenceThreshold { get; init; } = AiProposalGate.DefaultConfidenceThreshold;
}

public sealed record VoiceAiDecisionResult(
    AiGateOutcome Outcome,
    Order? Order,
    string? Reason,
    AiProposedOutcome ProposedOutcome,
    double Confidence,
    string Provider,
    string RawResponse);

public sealed class VoiceAiDecisionService(
    IConversationModel model,
    AiProposalGate gate,
    HandoffContextService? handoff = null)
{
    public async Task<VoiceAiDecisionResult> DecideAsync(
        VoiceAiDecisionCommand command,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(command.Transcript);

        var transcript = FinalTranscript(command.Transcript);
        var state = new ConversationState(
            [
                new ConversationTurn("system",
                    "Decide the order-confirmation outcome from the caller transcript. " +
                    "Return JSON only: {\"outcome\":\"confirm|reject|needs_human|reschedule\",\"confidence\":0.0}. " +
                    "Only use confirm when the customer clearly confirms. Unclear, partial, " +
                    "or operational requests must be needs_human. Rejections are proposals; " +
                    "the workflow gate decides whether a machine may apply them."),
                new ConversationTurn("user", transcript),
            ],
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["merchantId"] = command.MerchantId.ToString("D"),
                ["orderId"] = command.OrderId.ToString("D"),
                ["callSessionId"] = command.CallSessionId.ToString("D"),
            });

        var raw = await ReadResponseAsync(state, ct).ConfigureAwait(false);
        var decision = VoiceModelDecision.Parse(raw);

        var result = await gate.ApplyAsync(new AiProposal
        {
            MerchantId = command.MerchantId,
            OrderId = command.OrderId,
            Outcome = decision.Outcome,
            Confidence = decision.Confidence,
            ConfidenceThreshold = command.ConfidenceThreshold,
        }, ct).ConfigureAwait(false);

        if (handoff is not null
            && result.Outcome == AiGateOutcome.SentToReview
            && result.Order is not null)
        {
            await handoff.RecordAsync(new HandoffContextCommand
            {
                MerchantId = command.MerchantId,
                OrderId = command.OrderId,
                CallSessionId = command.CallSessionId,
                TranscriptText = transcript,
                HandoffReason = result.Reason ?? "ai sent call to human review",
            }, ct).ConfigureAwait(false);
        }

        return new VoiceAiDecisionResult(
            result.Outcome,
            result.Order,
            result.Reason,
            decision.Outcome,
            decision.Confidence,
            model.ProviderName,
            raw);
    }

    private async Task<string> ReadResponseAsync(ConversationState state, CancellationToken ct)
    {
        var output = new StringBuilder();

        await foreach (var chunk in model.RespondAsync(state, ct).ConfigureAwait(false))
        {
            if (chunk is TextDelta text)
                output.Append(text.Text);
        }

        return output.ToString();
    }

    private static string FinalTranscript(IReadOnlyList<Transcript> transcript)
    {
        var final = transcript
            .Where(t => t.IsFinal && !string.IsNullOrWhiteSpace(t.Text))
            .Select(t => t.Text.Trim())
            .ToArray();

        if (final.Length > 0)
            return string.Join(Environment.NewLine, final);

        return string.Join(Environment.NewLine, transcript
            .Where(t => !string.IsNullOrWhiteSpace(t.Text))
            .Select(t => t.Text.Trim()));
    }
}

internal sealed record VoiceModelDecision(AiProposedOutcome Outcome, double Confidence)
{
    public static VoiceModelDecision Parse(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return NeedsHuman();

        var json = ExtractJsonObject(raw);
        if (json is null)
            return NeedsHuman();

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            var outcome = root.TryGetProperty("outcome", out var outcomeElement)
                ? ParseOutcome(outcomeElement.GetString())
                : AiProposedOutcome.NeedsHuman;

            var confidence = root.TryGetProperty("confidence", out var confidenceElement)
                ? ParseConfidence(confidenceElement)
                : 0d;

            return new VoiceModelDecision(outcome, Math.Clamp(confidence, 0d, 1d));
        }
        catch (JsonException)
        {
            return NeedsHuman();
        }
    }

    private static string? ExtractJsonObject(string raw)
    {
        var start = raw.IndexOf('{');
        var end = raw.LastIndexOf('}');

        return start >= 0 && end > start
            ? raw[start..(end + 1)]
            : null;
    }

    private static AiProposedOutcome ParseOutcome(string? value) =>
        value?.Trim().ToLowerInvariant() switch
        {
            "confirm" or "confirmed" or "accept" or "accepted" => AiProposedOutcome.Confirm,
            "reject" or "rejected" or "cancel" or "cancelled" or "canceled" => AiProposedOutcome.Reject,
            "reschedule" or "later" => AiProposedOutcome.Reschedule,
            _ => AiProposedOutcome.NeedsHuman,
        };

    private static double ParseConfidence(JsonElement element) =>
        element.ValueKind switch
        {
            JsonValueKind.Number when element.TryGetDouble(out var value) => value,
            JsonValueKind.String when double.TryParse(
                element.GetString(),
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out var value) => value,
            _ => 0d,
        };

    private static VoiceModelDecision NeedsHuman() =>
        new(AiProposedOutcome.NeedsHuman, 1d);
}
