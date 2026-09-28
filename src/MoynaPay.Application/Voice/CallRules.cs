using System.Globalization;
using System.Text;
using MoynaPay.Domain.Orders;

namespace MoynaPay.Application.Voice;

/// <summary>
/// When it is acceptable to ring someone.
///
/// A confirmation call at two in the morning is a complaint, an uninstall, and the end of
/// that merchant's trust in the product. The window is not a throughput setting to be
/// loosened on a busy day.
///
/// Expressed in the merchant's own time, not the server's: a host in UTC must not shift
/// everybody's evening by six hours.
/// </summary>
public sealed class CallingHours
{
    public static readonly TimeSpan DhakaOffset = TimeSpan.FromHours(6);

    public TimeOnly Opens { get; init; } = new(9, 0);
    public TimeOnly Closes { get; init; } = new(21, 0);
    public TimeSpan Offset { get; init; } = DhakaOffset;

    public bool IsOpen(DateTimeOffset instant)
    {
        var local = TimeOnly.FromDateTime(instant.ToOffset(Offset).DateTime);

        return local >= Opens && local < Closes;
    }

    /// <summary>The next moment the window is open, which is now if it already is.</summary>
    public DateTimeOffset NextOpening(DateTimeOffset instant)
    {
        if (IsOpen(instant)) return instant;

        var local = instant.ToOffset(Offset);
        var today = new DateTimeOffset(local.Date, Offset) + Opens.ToTimeSpan();

        return local < today ? today : today.AddDays(1);
    }
}

/// <summary>
/// Whether to ring again, and when.
///
/// Spacing matters more than the count. Three calls inside a minute is harassment and gets
/// the number blocked; three across an afternoon is diligence. The gap widens because
/// somebody who missed two calls is probably busy, not absent.
/// </summary>
public sealed class RedialPolicy
{
    public int MaxAttempts { get; init; } = 3;

    public IReadOnlyList<TimeSpan> Gaps { get; init; } =
    [
        TimeSpan.FromMinutes(20),
        TimeSpan.FromHours(2),
    ];

    /// <summary>
    /// The earliest this order may be rung again, or null when the machine should stop and
    /// hand it to a person.
    /// </summary>
    public DateTimeOffset? NextAttemptAt(
        int attempts, DateTimeOffset? lastAttemptAt, CallingHours hours, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(hours);

        if (attempts >= MaxAttempts) return null;

        // An order nobody has rung yet waits for nothing. The gaps describe the space
        // between attempts, so the first only applies once there is an attempt to space
        // from - otherwise every new order sits idle for twenty minutes before its first
        // call, which is the delay this product exists to remove.
        var gap = attempts == 0 || Gaps.Count == 0
            ? TimeSpan.Zero
            : Gaps[Math.Min(attempts - 1, Gaps.Count - 1)];

        var earliest = (lastAttemptAt ?? now) + gap;

        return hours.NextOpening(earliest > now ? earliest : now);
    }
}

/// <summary>
/// The script, as text with holes in it.
///
/// A template rather than code because the sentence is the part every merchant wants to
/// change, and none of them should need a release to change it. It is also the part that
/// has to be written by someone who knows the customers - a clothes shop phrases a
/// cancellation differently from a pharmacy, and both are right.
///
/// Which key means what is data too. "Press 1 to confirm" is a convention, not a law.
/// </summary>
public sealed class CallScript
{
    public required string Greeting { get; init; }
    public required string Repeat { get; init; }
    public required string Confirmed { get; init; }
    public required string Rejected { get; init; }
    public required string Handover { get; init; }

    public IReadOnlyDictionary<string, CallOutcome> Keys { get; init; } =
        new Dictionary<string, CallOutcome>(StringComparer.Ordinal)
        {
            ["1"] = CallOutcome.Confirmed,
            ["0"] = CallOutcome.Rejected,
            ["9"] = CallOutcome.NeedsHuman,
        };

    /// <summary>How long to wait for a keypress, and how many times to ask.</summary>
    public TimeSpan AnswerTimeout { get; init; } = TimeSpan.FromSeconds(8);
    public int Repeats { get; init; } = 1;

    /// <summary>
    /// The order-confirmation script, in Bangla. A default, not a built-in: a merchant
    /// who sends their own never touches this.
    ///
    /// The amount is spoken in digits rather than words. Synthesised Bangla goes wrong on
    /// large numbers more often than on anything else, and a wrong amount on a
    /// confirmation call is worse than a clumsy one.
    /// </summary>
    public static readonly CallScript Default = new()
    {
        Greeting =
            "আসসালামু আলাইকুম। {দোকান} থেকে বলছি। " +
            "আপনার নামে একটি অর্ডার আছে, মোট {টাকা} টাকা। {পণ্য} " +
            "অর্ডারটি নিশ্চিত করতে ১ চাপুন। বাতিল করতে ০ চাপুন। " +
            "আমাদের একজন প্রতিনিধির সাথে কথা বলতে ৯ চাপুন।",

        Repeat = "আপনার উত্তর পাইনি। নিশ্চিত করতে ১, বাতিল করতে ০, প্রতিনিধির জন্য ৯ চাপুন।",

        Confirmed = "ধন্যবাদ। আপনার অর্ডার নিশ্চিত করা হয়েছে।",
        Rejected = "ঠিক আছে। আপনার অর্ডারটি বাতিল হিসেবে চিহ্নিত করা হলো। ধন্যবাদ।",
        Handover = "ঠিক আছে। আমাদের একজন প্রতিনিধি শীঘ্রই আপনাকে ফোন করবেন। ধন্যবাদ।",
    };
}

public static class ScriptRenderer
{
    /// <summary>
    /// Fills a script from the order.
    ///
    /// Numbers are rendered with the invariant culture on purpose. A server running under
    /// a Bangla locale would otherwise emit Bengali digits, and a speech model handed
    /// "১২৫০" reads it unpredictably - sometimes digit by digit, sometimes not at all.
    ///
    /// An unknown placeholder is left visible rather than blanked. A script reading "your
    /// order on {date}" out loud is obviously broken; one reading "your order on " sounds
    /// finished and is not.
    /// </summary>
    public static string Render(string template, Order order, string shopName)
    {
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(order);

        var amount = (order.ChargedAmount ?? order.Amount)
            .ToString("0.##", CultureInfo.InvariantCulture);

        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["দোকান"] = shopName,
            ["shop"] = shopName,
            ["নাম"] = order.CustomerName,
            ["name"] = order.CustomerName,
            ["টাকা"] = amount,
            ["amount"] = amount,
            ["পণ্য"] = order.Summary ?? "",
            ["summary"] = order.Summary ?? "",
            ["reference"] = order.Reference,
        };

        return Collapse(Substitute(template, values));
    }

    private static string Substitute(string template, IReadOnlyDictionary<string, string> values)
    {
        var output = new StringBuilder(template.Length + 32);
        var index = 0;

        while (index < template.Length)
        {
            var open = template.IndexOf('{', index);
            if (open < 0)
            {
                output.Append(template, index, template.Length - index);
                break;
            }

            var close = template.IndexOf('}', open + 1);
            if (close < 0)
            {
                output.Append(template, index, template.Length - index);
                break;
            }

            output.Append(template, index, open - index);

            var key = template[(open + 1)..close];
            output.Append(values.TryGetValue(key, out var value) ? value : template[open..(close + 1)]);

            index = close + 1;
        }

        return output.ToString();
    }

    /// <summary>
    /// An empty placeholder leaves a double space, which a speech model reads as a longer
    /// pause than intended. Cheap to tidy, and it is audible.
    /// </summary>
    private static string Collapse(string text)
    {
        var output = new StringBuilder(text.Length);
        var previousWasSpace = false;

        foreach (var c in text)
        {
            var isSpace = c == ' ';
            if (isSpace && previousWasSpace) continue;

            output.Append(c);
            previousWasSpace = isSpace;
        }

        return output.ToString().Trim();
    }
}

/// <summary>
/// The conversation, as a decision about keypresses.
///
/// Kept apart from the telephony on purpose. A call is slow, expensive and hard to
/// reproduce; the rules about what counts as an answer are none of those, and they are
/// the part that must never be wrong. Everything here is decided from a keypress and a
/// count, so the whole script can be run thousands of times without a phone ringing.
///
/// One rule shapes it: the machine records an answer, it never invents one. Silence is
/// not consent and a wrong key twice is not a decision - both go to a person.
/// </summary>
public static class IvrDecision
{
    /// <summary>
    /// What a keypress means on this script, or null when it means nothing and the
    /// question should be asked again.
    /// </summary>
    public static CallOutcome? Read(CallScript script, string? digit)
    {
        ArgumentNullException.ThrowIfNull(script);

        if (digit is null) return null;

        return script.Keys.TryGetValue(digit, out var outcome) ? outcome : null;
    }

    /// <summary>What to say after an outcome, before the line drops.</summary>
    public static string Closing(CallScript script, CallOutcome outcome)
    {
        ArgumentNullException.ThrowIfNull(script);

        return outcome switch
        {
            CallOutcome.Confirmed => script.Confirmed,
            CallOutcome.Rejected => script.Rejected,
            _ => script.Handover,
        };
    }

    /// <summary>
    /// The outcome when the prompts ran out. Always NeedsHuman - heard, but never
    /// answered, is not a customer saying no.
    /// </summary>
    public const CallOutcome Exhausted = CallOutcome.NeedsHuman;
}
