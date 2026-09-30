using MoynaPay.Domain.Orders;

namespace MoynaPay.Application.Voice;

public enum CallStep
{
    /// <summary>Nothing has been said yet.</summary>
    New = 0,

    /// <summary>The greeting, or a repeat of it, is playing.</summary>
    Asking = 1,

    /// <summary>The question has been asked and we are listening.</summary>
    Listening = 2,

    /// <summary>The last thing we will say is playing.</summary>
    Closing = 3,

    /// <summary>Said and done. Outcome carries what the call decided.</summary>
    Done = 4,
}

public enum CallActionKind
{
    /// <summary>Say this, then tell the flow the playback finished.</summary>
    Play = 0,

    /// <summary>Wait this long for a key, then tell the flow it timed out.</summary>
    Listen = 1,

    /// <summary>Put the phone down. Outcome is what the call came to.</summary>
    Hangup = 2,

    /// <summary>Nothing to do. An event that arrived out of turn, and was ignored.</summary>
    Nothing = 3,
}

public readonly record struct CallAction(
    CallActionKind Kind, string? Say, TimeSpan? For, CallOutcome? Outcome);

/// <summary>
/// One call, as a sequence of things to say and moments to listen.
///
/// Pure: it is handed events and returns the next action, and it neither plays audio nor
/// knows what a channel is. That is what makes the whole conversation - greeting, silence,
/// a repeat, more silence, giving up - checkable in a millisecond, when running it for real
/// means ringing a phone and waiting eight seconds twice.
///
/// Two things it must never do, both of which cost money:
///
///   The closing line always plays before the line drops. Hanging up the instant a customer
///   presses 1 is how a merchant discovers we are cutting their customers off mid-sentence.
///
///   Running out of prompts is NeedsHuman, never Rejected. A customer who heard the
///   question and did not answer has not said no.
/// </summary>
public sealed class CallFlow
{
    private readonly CallScript _script;
    private readonly Order _order;
    private readonly string _shopName;

    private int _asked;

    public CallFlow(CallScript script, Order order, string shopName)
    {
        ArgumentNullException.ThrowIfNull(script);
        ArgumentNullException.ThrowIfNull(order);

        _script = script;
        _order = order;
        _shopName = shopName;
    }

    public CallStep Step { get; private set; } = CallStep.New;

    public CallOutcome? Outcome { get; private set; }

    /// <summary>How many times the question has been asked, greeting included.</summary>
    public int Asked => _asked;

    /// <summary>The call has been answered. Say the greeting.</summary>
    public CallAction Begin()
    {
        if (Step != CallStep.New) return Nothing;

        _asked = 1;
        Step = CallStep.Asking;

        return Say(_script.Greeting);
    }

    /// <summary>Whatever we were saying has finished.</summary>
    public CallAction Said()
    {
        switch (Step)
        {
            case CallStep.Asking:
                Step = CallStep.Listening;
                return new CallAction(CallActionKind.Listen, null, _script.AnswerTimeout, null);

            case CallStep.Closing:
                Step = CallStep.Done;
                return new CallAction(CallActionKind.Hangup, null, null, Outcome);

            default:
                return Nothing;
        }
    }

    /// <summary>
    /// A key that means something was pressed.
    ///
    /// Accepted while the greeting is still playing, not only while listening: a customer
    /// who has heard this prompt before presses 1 over the top of it, and refusing that
    /// answer makes them sit through a recording to say something they already said.
    /// </summary>
    public CallAction Pressed(CallOutcome outcome)
    {
        if (Step is not (CallStep.Asking or CallStep.Listening)) return Nothing;

        Outcome = outcome;
        Step = CallStep.Closing;

        return Say(IvrDecision.Closing(_script, outcome));
    }

    /// <summary>Nobody pressed anything in time.</summary>
    public CallAction Waited()
    {
        if (Step != CallStep.Listening) return Nothing;

        // One more ask, if the script allows one. Repeats is how many times to ask AGAIN,
        // so the greeting plus Repeats is the total.
        if (_asked <= _script.Repeats)
        {
            _asked++;
            Step = CallStep.Asking;

            return Say(_script.Repeat);
        }

        Outcome = IvrDecision.Exhausted;
        Step = CallStep.Closing;

        return Say(IvrDecision.Closing(_script, IvrDecision.Exhausted));
    }

    /// <summary>
    /// The customer hung up, or the line dropped.
    ///
    /// Whatever was decided stands - a customer who pressed 1 and then put the phone down
    /// has confirmed. Nothing decided stays nothing decided, which a caller must not read
    /// as a rejection.
    /// </summary>
    public CallOutcome? Ended()
    {
        Step = CallStep.Done;

        return Outcome;
    }

    private CallAction Say(string template) =>
        new(CallActionKind.Play, ScriptRenderer.Render(template, _order, _shopName), null, null);

    private static CallAction Nothing => new(CallActionKind.Nothing, null, null, null);
}
