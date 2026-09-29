using MoynaPay.Application.Voice.Ari;

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
            var id = calls.Register(Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), now);

            return !string.IsNullOrWhiteSpace(id) && calls.TryGetByChannel(id, out _);
        });

        check("the channel we chose names the order it is about", () =>
        {
            var calls = new CallCorrelator();
            var order = Guid.CreateVersion7();
            var id = calls.Register(Guid.CreateVersion7(), Guid.CreateVersion7(), order, now);

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
            var id = calls.Register(session, Guid.CreateVersion7(), Guid.CreateVersion7(), now);

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
            var id = calls.Register(Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), now);

            try
            {
                calls.Register(Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), now, id);
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
            calls.Register(session, Guid.CreateVersion7(), Guid.CreateVersion7(), now);

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
            var id = calls.Register(session, Guid.CreateVersion7(), Guid.CreateVersion7(), now);

            return calls.Release(session) is not null
                && !calls.TryGetByChannel(id, out _)
                && !calls.TryGetBySession(session, out _)
                && calls.Count == 0;
        });

        check("releasing twice is harmless", () =>
        {
            var calls = new CallCorrelator();
            var session = Guid.CreateVersion7();
            calls.Register(session, Guid.CreateVersion7(), Guid.CreateVersion7(), now);

            return calls.Release(session) is not null && calls.Release(session) is null;
        });

        check("releasing by channel says which call it was", () =>
        {
            var calls = new CallCorrelator();
            var order = Guid.CreateVersion7();
            var id = calls.Register(Guid.CreateVersion7(), Guid.CreateVersion7(), order, now);

            return calls.ReleaseByChannel(id)?.OrderId == order;
        });

        // An originate that fails outright leaves a registration nothing will ever release.
        // Without the sweep those accumulate for the life of the process.
        check("a call that was never seen is swept", () =>
        {
            var calls = new CallCorrelator();
            calls.Register(Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), now);

            var dropped = calls.Sweep(now.AddMinutes(11), TimeSpan.FromMinutes(10));

            return dropped.Count == 1 && calls.Count == 0;
        });

        check("a call still in progress is not swept", () =>
        {
            var calls = new CallCorrelator();
            calls.Register(Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), now);

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

            var idA = calls.Register(a, Guid.CreateVersion7(), orderA, now);
            var idB = calls.Register(b, Guid.CreateVersion7(), orderB, now);

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
