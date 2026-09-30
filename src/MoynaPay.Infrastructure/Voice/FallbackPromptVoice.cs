using MoynaPay.Application.Abstractions;

namespace MoynaPay.Infrastructure.Voice;

public sealed class FallbackPromptVoice(string media) : IPromptVoice
{
    public Task<string> MediaForAsync(string text, CancellationToken ct = default) =>
        Task.FromResult(media);
}
