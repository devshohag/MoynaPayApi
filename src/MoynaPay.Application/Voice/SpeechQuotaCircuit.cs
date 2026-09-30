using MoynaPay.Application.Abstractions;

namespace MoynaPay.Application.Voice;

public sealed class SpeechQuotaCircuit(IClock clock, TimeSpan cooldown)
{
    private long _openUntilUnixMilliseconds;

    public bool IsOpen => OpenUntil > clock.UtcNow;

    public DateTimeOffset OpenUntil =>
        DateTimeOffset.FromUnixTimeMilliseconds(Interlocked.Read(ref _openUntilUnixMilliseconds));

    public void Open()
    {
        var until = clock.UtcNow.Add(cooldown).ToUnixTimeMilliseconds();

        while (true)
        {
            var current = Interlocked.Read(ref _openUntilUnixMilliseconds);
            if (current >= until) return;
            if (Interlocked.CompareExchange(ref _openUntilUnixMilliseconds, until, current) == current)
                return;
        }
    }
}
