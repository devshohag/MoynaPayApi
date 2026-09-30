using System.Collections.Concurrent;
using MoynaPay.Domain.Orders;

namespace MoynaPay.Application.Voice;

public enum KeypressOutcome
{
    /// <summary>A key that means something. Decision carries what it meant.</summary>
    Accepted = 0,

    /// <summary>A key with no meaning in this script. Not an answer, and not a refusal.</summary>
    Unknown = 1,

    /// <summary>Enough wrong keys that asking again is not going to help.</summary>
    Exhausted = 2,

    /// <summary>This call already has an answer, or is not one we are listening to.</summary>
    Ignored = 3,
}

public readonly record struct Keypress(
    KeypressOutcome Outcome, CallOutcome? Decision, int UnknownPresses);

/// <summary>
/// What the customer pressed, and when that becomes an answer.
///
/// The rules here are small and they are all about not turning noise into a decision. An
/// order that is wrongly confirmed ships goods nobody ordered; an order that is wrongly
/// rejected loses a sale that was already made. Both are worse than sending the order to a
/// person, which is why every uncertain path here ends at NeedsHuman and none of them ends
/// at Rejected.
///
/// Pure and in-process: a keypress is only interesting while the call is up, and a call that
/// outlives this worker is a call whose channel is gone anyway.
/// </summary>
public sealed class DtmfCollector
{
    /// <summary>
    /// Wrong keys before we stop asking. Three is enough to cover a mis-hit and a retry;
    /// more than that and the customer has not understood the prompt, which is a person's
    /// problem, not a louder prompt's.
    /// </summary>
    public const int MaxUnknownPresses = 3;

    /// <summary>
    /// Asterisk can report one keypress twice - a long press, a repeated frame - and two
    /// reports of the same digit inside this window are one press. Without it, a customer
    /// leaning on the 5 key exhausts their three tries in a quarter of a second.
    /// </summary>
    public static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(250);

    private sealed class Listening
    {
        public CallOutcome? Decision;
        public int UnknownPresses;
        public string? LastDigit;
        public DateTimeOffset LastPressAt;
    }

    private readonly ConcurrentDictionary<string, Listening> _channels = new(StringComparer.Ordinal);

    public int Count => _channels.Count;

    /// <summary>Begins listening on a channel. Pressing keys on a channel we never started
    /// listening to is ignored, which is how somebody else's call stays somebody else's.</summary>
    public void Listen(string channelId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(channelId);

        _channels[channelId] = new Listening();
    }

    public Keypress Press(string channelId, string? digit, CallScript script, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(script);

        if (string.IsNullOrEmpty(channelId) || !_channels.TryGetValue(channelId, out var state))
        {
            return new Keypress(KeypressOutcome.Ignored, null, 0);
        }

        lock (state)
        {
            // The first answer is the answer.
            //
            // A thumb that slides from 1 to 0 has already confirmed, and treating the second
            // key as a change of mind turns a confirmed order into a rejection - which the
            // merchant then has to find and undo. Later keys are heard and discarded.
            if (state.Decision is { } already)
            {
                return new Keypress(KeypressOutcome.Ignored, already, state.UnknownPresses);
            }

            var pressed = digit?.Trim();

            if (string.IsNullOrEmpty(pressed))
            {
                return new Keypress(KeypressOutcome.Unknown, null, state.UnknownPresses);
            }

            // One key reported twice is one key.
            if (string.Equals(pressed, state.LastDigit, StringComparison.Ordinal)
                && now - state.LastPressAt < Debounce)
            {
                return new Keypress(KeypressOutcome.Ignored, null, state.UnknownPresses);
            }

            state.LastDigit = pressed;
            state.LastPressAt = now;

            if (IvrDecision.Read(script, pressed) is { } outcome)
            {
                state.Decision = outcome;

                return new Keypress(KeypressOutcome.Accepted, outcome, state.UnknownPresses);
            }

            state.UnknownPresses++;

            // Out of tries. NeedsHuman, never Rejected: a customer who kept pressing the
            // wrong key was trying to answer, and reading that as "no" deletes a real sale.
            return state.UnknownPresses >= MaxUnknownPresses
                ? new Keypress(KeypressOutcome.Exhausted, IvrDecision.Exhausted, state.UnknownPresses)
                : new Keypress(KeypressOutcome.Unknown, null, state.UnknownPresses);
        }
    }

    /// <summary>The answer this call reached, if it reached one.</summary>
    public bool Decided(string? channelId, out CallOutcome outcome)
    {
        outcome = default;

        if (string.IsNullOrEmpty(channelId) || !_channels.TryGetValue(channelId, out var state))
        {
            return false;
        }

        lock (state)
        {
            if (state.Decision is not { } decision) return false;

            outcome = decision;
            return true;
        }
    }

    /// <summary>
    /// Stops listening, and says what the call ended with.
    ///
    /// Null means nobody pressed anything usable - which is not a rejection and not a
    /// confirmation, and is the caller's cue to hand the order to a person.
    /// </summary>
    public CallOutcome? Forget(string? channelId)
    {
        if (string.IsNullOrEmpty(channelId) || !_channels.TryRemove(channelId, out var state))
        {
            return null;
        }

        lock (state)
        {
            return state.Decision;
        }
    }
}
