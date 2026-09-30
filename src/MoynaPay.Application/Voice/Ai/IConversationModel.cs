namespace MoynaPay.Application.Voice.Ai;

public sealed record ConversationTurn(string Role, string Text);

public sealed record ConversationState(
    IReadOnlyList<ConversationTurn> History,
    IReadOnlyDictionary<string, string> Metadata);

public abstract record ModelChunk;

public sealed record TextDelta(string Text) : ModelChunk;

public sealed record ModelCompleted(string FinishReason) : ModelChunk;

/// <summary>
/// Produces the next conversational answer or decision. A Gemini-backed implementation can
/// sit behind this port; tests can use a deterministic model.
/// </summary>
public interface IConversationModel
{
    string ProviderName { get; }

    IAsyncEnumerable<ModelChunk> RespondAsync(ConversationState state, CancellationToken ct);
}
