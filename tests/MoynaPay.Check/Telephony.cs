using MoynaPay.Application.Voice;
using MoynaPay.Application.Voice.Ari;
using MoynaPay.Domain.Orders;

namespace MoynaPay.Check;

/// <summary>
/// Phase 19: the event stream and knowing whose channel is whose.
///
/// None of this can be tried by hand without an Asterisk, a trunk and somebody's phone
/// ringing - and the parts that matter are the ones that only happen on a bad day: a frame
/// that does not parse, a socket that drops, a channel that arrives before the request that
/// asked for it came back. So they are all asserted here, against a clock that is a variable
/// and JSON that is a string.
/// </summary>
internal static class Telephony
{
    public static void Run(Action<string, Func<bool>> check)
    {
        ArgumentNullException.ThrowIfNull(check);

        Events(check);
        Correlation(check);
        Reconnect(check);
        Keypresses(check);
        Conversation(check);
    }

    // -----------------------------------------------------------------------
    // The whole call, as a conversation
    //
    // Running one of these for real means ringing a phone and sitting through eight
    // seconds of silence twice. All of it happens here instead, in a millisecond.
    // -----------------------------------------------------------------------
    private static void Conversation(Action<string, Func<bool>> check)
    {
        static Order Sample() => new()
        {
            TenantId = Guid.CreateVersion7(),
            Reference = "ORD-7",
            CustomerName = "রফিক",
            Msisdn = "8801711223344",
            Amount = 1250m,
            Summary = "দুইটি শার্ট",
        };

        static CallFlow New(CallScript? script = null) =>
            new(script ?? CallScript.Default, Sample(), "নীল দোকান");

        check("the call opens with the greeting", () =>
            New().Begin() is { Kind: CallActionKind.Play, Say: not null });

        check("the greeting has the shop, the amount and the goods in it", () =>
        {
            var said = New().Begin().Say!;

            return said.Contains("নীল দোকান", StringComparison.Ordinal)
                && said.Contains("1250", StringComparison.Ordinal)
                && said.Contains("দুইটি শার্ট", StringComparison.Ordinal);
        });

        // A server under a Bangla locale would otherwise substitute "১২৫০", which a speech
        // model reads unpredictably - and a wrong amount on a confirmation call is worse
        // than a clumsy one. The script's own "১ চাপুন" is written that way on purpose and
        // is none of this check's business, so the assertion is about the amount alone.
        check("the amount is substituted in western digits", () =>
        {
            var said = New().Begin().Say!;

            return said.Contains("1250", StringComparison.Ordinal)
                && !said.Contains("১২৫০", StringComparison.Ordinal);
        });

        check("after the greeting we listen", () =>
        {
            var flow = New();
            flow.Begin();

            return flow.Said() is { Kind: CallActionKind.Listen, For: not null }
                && flow.Step == CallStep.Listening;
        });

        check("beginning twice does nothing the second time", () =>
        {
            var flow = New();
            flow.Begin();

            return flow.Begin().Kind == CallActionKind.Nothing;
        });

        // The rule a merchant notices when it is broken.
        check("the closing line plays before the line drops", () =>
        {
            var flow = New();
            flow.Begin();
            flow.Said();

            var closing = flow.Pressed(CallOutcome.Confirmed);

            return closing.Kind == CallActionKind.Play
                && closing.Say == CallScript.Default.Confirmed
                && flow.Said().Kind == CallActionKind.Hangup;
        });

        check("the hangup carries what the call decided", () =>
        {
            var flow = New();
            flow.Begin();
            flow.Said();
            flow.Pressed(CallOutcome.Confirmed);

            return flow.Said().Outcome == CallOutcome.Confirmed && flow.Step == CallStep.Done;
        });

        check("each answer gets its own closing line", () =>
        {
            var reject = New();
            reject.Begin();
            reject.Said();

            var human = New();
            human.Begin();
            human.Said();

            return reject.Pressed(CallOutcome.Rejected).Say == CallScript.Default.Rejected
                && human.Pressed(CallOutcome.NeedsHuman).Say == CallScript.Default.Handover;
        });

        // A customer who has heard this prompt before presses 1 over the top of it.
        check("a key pressed over the greeting is taken", () =>
        {
            var flow = New();
            flow.Begin();

            return flow.Pressed(CallOutcome.Confirmed).Kind == CallActionKind.Play
                && flow.Outcome == CallOutcome.Confirmed;
        });

        check("a key pressed after the closing started is ignored", () =>
        {
            var flow = New();
            flow.Begin();
            flow.Said();
            flow.Pressed(CallOutcome.Confirmed);

            return flow.Pressed(CallOutcome.Rejected).Kind == CallActionKind.Nothing
                && flow.Outcome == CallOutcome.Confirmed;
        });

        check("silence brings the question again", () =>
        {
            var flow = New();
            flow.Begin();
            flow.Said();

            var again = flow.Waited();

            return again.Kind == CallActionKind.Play
                && again.Say == CallScript.Default.Repeat
                && flow.Asked == 2;
        });

        // The rule the whole product rests on: heard but never answered is not "no".
        check("running out of prompts ends at a person, not a rejection", () =>
        {
            var flow = New();
            flow.Begin();
            flow.Said();
            flow.Waited();      // the repeat
            flow.Said();
            var giveUp = flow.Waited();

            return giveUp.Kind == CallActionKind.Play
                && giveUp.Say == CallScript.Default.Handover
                && flow.Outcome == CallOutcome.NeedsHuman
                && flow.Outcome != CallOutcome.Rejected;
        });

        check("the script decides how many times to ask", () =>
        {
            var once = new CallScript
            {
                Greeting = "g", Repeat = "r", Confirmed = "c", Rejected = "x", Handover = "h",
                Repeats = 0,
            };

            var flow = New(once);
            flow.Begin();
            flow.Said();

            // No repeats allowed, so the first silence is the last.
            return flow.Waited().Say == "h" && flow.Outcome == CallOutcome.NeedsHuman;
        });

        check("a timeout while nothing is playing does nothing", () =>
        {
            var flow = New();
            flow.Begin();

            return flow.Waited().Kind == CallActionKind.Nothing;
        });

        check("a customer who hangs up after answering has still answered", () =>
        {
            var flow = New();
            flow.Begin();
            flow.Said();
            flow.Pressed(CallOutcome.Confirmed);

            return flow.Ended() == CallOutcome.Confirmed;
        });

        check("a customer who hangs up saying nothing has decided nothing", () =>
        {
            var flow = New();
            flow.Begin();
            flow.Said();

            return flow.Ended() is null;
        });

        check("nothing happens after the call is done", () =>
        {
            var flow = New();
            flow.Begin();
            flow.Ended();

            return flow.Said().Kind == CallActionKind.Nothing
                && flow.Pressed(CallOutcome.Confirmed).Kind == CallActionKind.Nothing
                && flow.Waited().Kind == CallActionKind.Nothing;
        });
    }

    // -----------------------------------------------------------------------
    // What the customer pressed
    //
    // Every rule below exists to stop noise becoming a decision. A wrongly confirmed
    // order ships goods nobody asked for; a wrongly rejected one deletes a sale that
    // was already made. Both are worse than handing the order to a person.
    // -----------------------------------------------------------------------
    private static void Keypresses(Action<string, Func<bool>> check)
    {
        var script = CallScript.Default;
        var now = new DateTimeOffset(2026, 9, 29, 12, 0, 0, TimeSpan.Zero);

        static DtmfCollector Listening(string channel)
        {
            var collector = new DtmfCollector();
            collector.Listen(channel);
            return collector;
        }

        check("1 confirms", () =>
            Listening("c").Press("c", "1", script, now)
                is { Outcome: KeypressOutcome.Accepted, Decision: CallOutcome.Confirmed });

        check("0 rejects", () =>
            Listening("c").Press("c", "0", script, now)
                is { Outcome: KeypressOutcome.Accepted, Decision: CallOutcome.Rejected });

        check("9 asks for a person", () =>
            Listening("c").Press("c", "9", script, now)
                is { Outcome: KeypressOutcome.Accepted, Decision: CallOutcome.NeedsHuman });

        // The rule that stops a slipped thumb from cancelling a confirmed order.
        check("the first answer is the answer", () =>
        {
            var keys = Listening("c");
            keys.Press("c", "1", script, now);

            var second = keys.Press("c", "0", script, now.AddSeconds(1));

            return second.Outcome == KeypressOutcome.Ignored
                && keys.Decided("c", out var decision)
                && decision == CallOutcome.Confirmed;
        });

        check("a key on a channel we are not listening to does nothing", () =>
            new DtmfCollector().Press("someone-elses-channel", "1", script, now).Outcome
                == KeypressOutcome.Ignored);

        check("a key on a null channel does nothing", () =>
            new DtmfCollector().Press(null!, "1", script, now).Outcome == KeypressOutcome.Ignored);

        check("a meaningless key is not an answer", () =>
            Listening("c").Press("c", "5", script, now)
                is { Outcome: KeypressOutcome.Unknown, Decision: null });

        check("a star is not an answer", () =>
            Listening("c").Press("c", "*", script, now).Decision is null);

        check("an empty digit is not an answer", () =>
            Listening("c").Press("c", "", script, now).Decision is null);

        // Asterisk repeats a long press. Without the debounce, a customer resting a finger
        // on 5 burns all three tries in a quarter of a second.
        check("one key reported twice is one key", () =>
        {
            var keys = Listening("c");
            keys.Press("c", "5", script, now);

            var repeat = keys.Press("c", "5", script, now.AddMilliseconds(50));

            return repeat.Outcome == KeypressOutcome.Ignored && repeat.UnknownPresses == 1;
        });

        check("the same key pressed again later is a new press", () =>
        {
            var keys = Listening("c");
            keys.Press("c", "5", script, now);

            var again = keys.Press("c", "5", script, now.AddSeconds(2));

            return again.UnknownPresses == 2;
        });

        // The rule the product cannot survive breaking: the machine never decides "no".
        check("wrong keys end at a person, never at a rejection", () =>
        {
            var keys = Listening("c");

            keys.Press("c", "5", script, now);
            keys.Press("c", "6", script, now.AddSeconds(1));

            var third = keys.Press("c", "7", script, now.AddSeconds(2));

            return third.Outcome == KeypressOutcome.Exhausted
                && third.Decision == CallOutcome.NeedsHuman
                && third.Decision != CallOutcome.Rejected;
        });

        check("two wrong keys are not yet exhausted", () =>
        {
            var keys = Listening("c");
            keys.Press("c", "5", script, now);

            return keys.Press("c", "6", script, now.AddSeconds(1)).Outcome
                == KeypressOutcome.Unknown;
        });

        check("a wrong key does not stop a right one", () =>
        {
            var keys = Listening("c");
            keys.Press("c", "5", script, now);

            return keys.Press("c", "1", script, now.AddSeconds(1)).Decision == CallOutcome.Confirmed;
        });

        check("a call with no keypress has no answer", () =>
            !Listening("c").Decided("c", out _));

        check("forgetting a call hands back its answer", () =>
        {
            var keys = Listening("c");
            keys.Press("c", "1", script, now);

            return keys.Forget("c") == CallOutcome.Confirmed && keys.Count == 0;
        });

        // Silence is not a rejection. It is the absence of an answer, and the caller has to
        // be able to tell the two apart.
        check("forgetting a silent call hands back nothing", () =>
            Listening("c").Forget("c") is null);

        check("forgetting a call we never had is harmless", () =>
            new DtmfCollector().Forget("c") is null);

        check("two calls do not hear each other's keys", () =>
        {
            var keys = new DtmfCollector();
            keys.Listen("a");
            keys.Listen("b");

            keys.Press("a", "1", script, now);

            return keys.Decided("a", out var first) && first == CallOutcome.Confirmed
                && !keys.Decided("b", out _);
        });
    }

    // -----------------------------------------------------------------------
    // Parsing what Asterisk sends
    // -----------------------------------------------------------------------
    private static void Events(Action<string, Func<bool>> check)
    {
        const string stasisStart = """
            {"type":"StasisStart","application":"moynapay","args":[],
             "channel":{"id":"moynapay-0199abc","name":"PJSIP/bd-trunk-00000001",
                        "state":"Up","caller":{"name":"","number":"09610000000"}}}
            """;

        const string dtmf = """
            {"type":"ChannelDtmfReceived","application":"moynapay","digit":"1",
             "duration_ms":100,"channel":{"id":"moynapay-0199abc","state":"Up"}}
            """;

        check("a StasisStart is parsed", () =>
            AriEvent.TryParse(stasisStart) is { Type: AriEvent.StasisStart, ChannelId: "moynapay-0199abc" });

        check("a keypress is readable from the raw json", () =>
            AriEvent.TryParse(dtmf)?.Read("digit") == "1");

        check("a field that is not there reads as null", () =>
            AriEvent.TryParse(dtmf)?.Read("playback", "id") is null);

        check("a nested field is readable", () =>
            AriEvent.TryParse(stasisStart)?.Read("channel", "name") == "PJSIP/bd-trunk-00000001");

        check("a playback id is parsed", () =>
            AriEvent.TryParse("""{"type":"PlaybackFinished","playback":{"id":"pb1","state":"done"}}""")
                is { Type: AriEvent.PlaybackFinished, PlaybackId: "pb1" });

        check("a recording name is parsed", () =>
            AriEvent.TryParse("""{"type":"RecordingFinished","recording":{"name":"r1"}}""")
                ?.RecordingName == "r1");

        check("an event with no channel parses anyway", () =>
            AriEvent.TryParse("""{"type":"ApplicationReplaced"}""")
                is { Type: "ApplicationReplaced", ChannelId: null });

        // One truncated frame must cost one frame, not every call in progress.
        check("truncated json is refused, not thrown", () =>
            AriEvent.TryParse("""{"type":"StasisStart","channel":{"id":"x""") is null);

        check("json that is not an object is refused", () => AriEvent.TryParse("[1,2,3]") is null);
        check("an event with no type is refused", () => AriEvent.TryParse("""{"channel":{"id":"x"}}""") is null);
        check("empty is refused", () => AriEvent.TryParse("") is null);
        check("null is refused", () => AriEvent.TryParse(null) is null);

        // Bangla in a caller name is ordinary here, and a parser that mangles UTF-8 would
        // show it mangled in the app's timeline.
        check("utf-8 survives the parse", () =>
            AriEvent.TryParse("""{"type":"StasisStart","channel":{"id":"c1","name":"রফিক"}}""")
                ?.Read("channel", "name") == "রফিক");
    }

    // -----------------------------------------------------------------------
    // Which call is this channel?
    // -----------------------------------------------------------------------
    private static void Correlation(Action<string, Func<bool>> check)
    {
        var now = new DateTimeOffset(2026, 9, 29, 12, 0, 0, TimeSpan.Zero);

        check("a channel id is claimed before the call is placed", () =>
        {
            var calls = new CallCorrelator();
            var id = calls.Register(Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), "Test Shop", now);

            return !string.IsNullOrWhiteSpace(id) && calls.TryGetByChannel(id, out _);
        });

        check("the channel we chose names the order it is about", () =>
        {
            var calls = new CallCorrelator();
            var order = Guid.CreateVersion7();
            var id = calls.Register(Guid.CreateVersion7(), Guid.CreateVersion7(), order, "Test Shop", now);

            return calls.TryGetByChannel(id, out var call) && call.OrderId == order;
        });

        // The whole point of choosing the id ourselves: an answer that arrives before the
        // originate's HTTP response is still recognised, because there was nothing to wait
        // for. Correlating on the id read from the response is a race that passes on a quiet
        // system and loses calls on a busy one.
        check("a channel that arrives early is still recognised", () =>
        {
            var calls = new CallCorrelator();
            var session = Guid.CreateVersion7();
            var id = calls.Register(session, Guid.CreateVersion7(), Guid.CreateVersion7(), "Test Shop", now);

            // No response has been processed - only the registration exists.
            return calls.TryGetByChannel(id, out var call) && call.CallSessionId == session;
        });

        check("a channel nobody placed is not ours", () =>
            !new CallCorrelator().TryGetByChannel("PJSIP/inbound-0000001", out _));

        check("a null channel id is not ours", () =>
            !new CallCorrelator().TryGetByChannel(null, out _));

        check("two calls cannot claim one channel id", () =>
        {
            var calls = new CallCorrelator();
            var id = calls.Register(Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), "Test Shop", now);

            try
            {
                calls.Register(Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), "Test Shop", now, id);
                return false;
            }
            catch (InvalidOperationException)
            {
                return true;
            }
        });

        // The fallback, for an Asterisk that ignores the supplied channel id.
        check("a channel under another id is adopted by its session variable", () =>
        {
            var calls = new CallCorrelator();
            var session = Guid.CreateVersion7();
            calls.Register(session, Guid.CreateVersion7(), Guid.CreateVersion7(), "Test Shop", now);

            return calls.TryAdopt("PJSIP/whatever-00000009", session, out var call)
                && call.ChannelId == "PJSIP/whatever-00000009"
                && calls.TryGetByChannel("PJSIP/whatever-00000009", out _);
        });

        check("a session we never placed is not adopted", () =>
            !new CallCorrelator().TryAdopt("PJSIP/x", Guid.CreateVersion7(), out _));

        check("releasing forgets the channel and the session", () =>
        {
            var calls = new CallCorrelator();
            var session = Guid.CreateVersion7();
            var id = calls.Register(session, Guid.CreateVersion7(), Guid.CreateVersion7(), "Test Shop", now);

            return calls.Release(session) is not null
                && !calls.TryGetByChannel(id, out _)
                && !calls.TryGetBySession(session, out _)
                && calls.Count == 0;
        });

        check("releasing twice is harmless", () =>
        {
            var calls = new CallCorrelator();
            var session = Guid.CreateVersion7();
            calls.Register(session, Guid.CreateVersion7(), Guid.CreateVersion7(), "Test Shop", now);

            return calls.Release(session) is not null && calls.Release(session) is null;
        });

        check("releasing by channel says which call it was", () =>
        {
            var calls = new CallCorrelator();
            var order = Guid.CreateVersion7();
            var id = calls.Register(Guid.CreateVersion7(), Guid.CreateVersion7(), order, "Test Shop", now);

            return calls.ReleaseByChannel(id)?.OrderId == order;
        });

        // An originate that fails outright leaves a registration nothing will ever release.
        // Without the sweep those accumulate for the life of the process.
        check("a call that was never seen is swept", () =>
        {
            var calls = new CallCorrelator();
            calls.Register(Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), "Test Shop", now);

            var dropped = calls.Sweep(now.AddMinutes(11), TimeSpan.FromMinutes(10));

            return dropped.Count == 1 && calls.Count == 0;
        });

        check("a call still in progress is not swept", () =>
        {
            var calls = new CallCorrelator();
            calls.Register(Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), "Test Shop", now);

            return calls.Sweep(now.AddMinutes(9), TimeSpan.FromMinutes(10)).Count == 0
                && calls.Count == 1;
        });

        check("two calls do not confuse each other", () =>
        {
            var calls = new CallCorrelator();
            var a = Guid.CreateVersion7();
            var b = Guid.CreateVersion7();
            var orderA = Guid.CreateVersion7();
            var orderB = Guid.CreateVersion7();

            var idA = calls.Register(a, Guid.CreateVersion7(), orderA, "Test Shop", now);
            var idB = calls.Register(b, Guid.CreateVersion7(), orderB, "Test Shop", now);

            calls.Release(a);

            return !calls.TryGetByChannel(idA, out _)
                && calls.TryGetByChannel(idB, out var still)
                && still.OrderId == orderB;
        });
    }

    // -----------------------------------------------------------------------
    // Coming back after the socket drops
    // -----------------------------------------------------------------------
    private static void Reconnect(Action<string, Func<bool>> check)
    {
        check("the first retry is immediate enough to matter", () =>
            AriReconnect.Delay(0) == AriReconnect.InitialDelay);

        check("the gap widens with each failure", () =>
            AriReconnect.Delay(1) < AriReconnect.Delay(2)
            && AriReconnect.Delay(2) < AriReconnect.Delay(3));

        // Every second the socket is down is a second of keypresses nobody hears, so the
        // ceiling is set by how long a customer holds the phone.
        check("the gap is capped at half a minute", () =>
            AriReconnect.Delay(50) == AriReconnect.MaxDelay
            && AriReconnect.MaxDelay <= TimeSpan.FromSeconds(30));

        check("a week of failures does not overflow", () =>
            AriReconnect.Delay(int.MaxValue) == AriReconnect.MaxDelay);

        // A nightly Asterisk restart must not leave us waiting thirty seconds every night.
        check("a connection that held for a while resets the backoff", () =>
            AriReconnect.ResetsBackoff(wasConnected: true, TimeSpan.FromMinutes(5)));

        check("a connection that dropped at once does not reset it", () =>
            !AriReconnect.ResetsBackoff(wasConnected: true, TimeSpan.FromSeconds(2)));

        check("never connecting does not reset it", () =>
            !AriReconnect.ResetsBackoff(wasConnected: false, TimeSpan.FromHours(1)));
    }
}
