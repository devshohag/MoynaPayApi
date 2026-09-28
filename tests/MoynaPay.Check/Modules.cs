using MoynaPay.Application.Payments.Invoicing;
using MoynaPay.Application.Payments.Parsing;
using MoynaPay.Application.Voice;
using MoynaPay.Domain.Orders;

namespace MoynaPay.Check;

/// <summary>
/// The two modules that came over from the other repositories.
///
/// Ported, not rewritten: the parser and the allocator are running code with real
/// messages behind them, and retyping them from memory would have thrown that away. What
/// changed is the namespace and the base class, so these assertions exist to prove the
/// move did not break anything - and, for the voice rules, to cover the decisions that
/// had to be written fresh because YoVoiceAgent never had them.
/// </summary>
internal static class Modules
{
    public static void Run(Action<string, Func<bool>> check)
    {
        ArgumentNullException.ThrowIfNull(check);

        Payments(check);
        Voice(check);
    }

    // -----------------------------------------------------------------------
    // Payment module
    // -----------------------------------------------------------------------
    private static void Payments(Action<string, Func<bool>> check)
    {
        var parser = MessageParser.ForBkash();

        var corpus = Path.Combine(AppContext.BaseDirectory, "fixtures", "bkash", "corpus.tsv");

        check("the bKash corpus travelled with the parser", () => File.Exists(corpus));

        // Every line is a message somebody actually received. Replaying them is the only
        // evidence that a parser change did not quietly stop recognising something.
        check("every corpus message is read as the kind it says it is", () =>
        {
            if (!File.Exists(corpus)) return false;

            var lines = File.ReadAllLines(corpus)
                .Where(l => !string.IsNullOrWhiteSpace(l) && !l.StartsWith('#'))
                .ToList();

            if (lines.Count < 20) return false;

            foreach (var line in lines)
            {
                var parts = line.Split('\t');
                if (parts.Length < 2) return false;

                if (!Enum.TryParse<MessageKind>(parts[0], out var expected)) return false;

                var parsed = parser.Parse("bKash", parts[1]);

                if (parsed.Kind != expected)
                {
                    Console.WriteLine($"        {expected} read as {parsed.Kind}: {parts[1][..Math.Min(60, parts[1].Length)]}");
                    return false;
                }
            }

            return true;
        });

        check("a received payment gives up its amount, id and time", () =>
        {
            var parsed = parser.Parse("bKash",
                "You have received Tk 200.00 from 01910126335. Fee Tk 0.00. " +
                "Balance Tk 1,881.86. TrxID DHD6EYO3HO at 13/08/2026 16:02");

            return parsed.Kind == MessageKind.P2PReceived
                && parsed.Amount == 200.00m
                && parsed.TrxId == "DHD6EYO3HO"
                && parsed.OccurredAt is not null;
        });

        // Dhaka time in, an instant out. Reading it as UTC would shift every payment six
        // hours and silently break every window comparison in the matcher.
        check("a bKash timestamp is read as Dhaka time and handed on as UTC", () =>
        {
            var at = BkashFieldReader.ReadTimestamp("13/08/2026 16:02");

            return at is { } value
                && value.Offset == TimeSpan.Zero
                && value.Hour == 10 && value.Minute == 2 && value.Day == 13;
        });

        // Thousands separators are commas. Getting this wrong turns a 15,000 taka deposit
        // into 15.
        check("a thousands separator is not a decimal point", () =>
            BkashFieldReader.ReadAmount("15,000.50") == 15000.50m
            && BkashFieldReader.ReadAmount("1,881.86") == 1881.86m);

        check("an unreadable message is unknown, never guessed at", () =>
        {
            var parsed = parser.Parse("bKash", "কিছু একটা হয়েছে কিন্তু কী জানি না");

            return parsed.Kind == MessageKind.Unknown && parsed.Confidence == 0m;
        });

        // The single most dangerous confusion in the whole module: an OTP message also
        // carries "Tk" and a number. Reading one as a payment ships an order nobody paid
        // for.
        check("an OTP is never a credit", () =>
        {
            foreach (var kind in new[]
            {
                MessageKind.Otp, MessageKind.CashOut, MessageKind.PaymentSent,
                MessageKind.PaymentReserved, MessageKind.BillPaid, MessageKind.Unknown,
            })
            {
                if (kind.IsCredit()) return false;
            }

            return MessageKind.P2PReceived.IsCredit()
                && MessageKind.CashIn.IsCredit()
                && MessageKind.BankDeposit.IsCredit();
        });

        // The salt is whole taka, at most two percent of the price and never above five.
        check("the charged amount goes up, never down", () =>
        {
            var allocated = AmountAllocator.Allocate(500m, [500m, 501m]);

            return allocated == 502m && allocated >= 500m;
        });

        check("a free figure is found when the first is taken", () =>
            AmountAllocator.Allocate(1250m, [1250m]) == 1251m);

        // Filling every slot is an ordinary afternoon on a busy wallet, not an error -
        // and widening the salt until the number stops resembling the price would be
        // worse than asking for a transaction id.
        check("when no distinct figure is left it gives up rather than distorting the price", () =>
        {
            var everySlot = Enumerable.Range(0, 6).Select(i => 500m + i).ToList();

            return AmountAllocator.Allocate(500m, everySlot) is null;
        });

        check("a small invoice gets at least one taka of room", () =>
            AmountAllocator.MaxSaltFor(50m) >= 1m
            && AmountAllocator.MaxSaltFor(100000m) == AmountAllocator.AbsoluteMaxSaltBdt);
    }

    // -----------------------------------------------------------------------
    // Voice module
    // -----------------------------------------------------------------------
    private static void Voice(Action<string, Func<bool>> check)
    {
        var hours = new CallingHours();
        var redial = new RedialPolicy();

        DateTimeOffset Dhaka(int h, int m = 0) =>
            new(2026, 9, 28, h, m, 0, CallingHours.DhakaOffset);

        check("the middle of the day is open", () => hours.IsOpen(Dhaka(13)));
        check("two in the morning is not", () => !hours.IsOpen(Dhaka(2)));
        check("nine at night is closed", () => !hours.IsOpen(Dhaka(21)));

        // A host in UTC must not shift everybody's evening. 03:00 UTC is 09:00 in Dhaka.
        check("the window follows Dhaka, not the server", () =>
            hours.IsOpen(new DateTimeOffset(2026, 9, 28, 3, 0, 0, TimeSpan.Zero)));

        check("a call due at night waits until morning", () =>
        {
            var next = hours.NextOpening(Dhaka(2));

            return next.ToOffset(CallingHours.DhakaOffset).Hour == 9 && next.Day == 28;
        });

        check("a call due late at night waits for tomorrow", () =>
        {
            var next = hours.NextOpening(Dhaka(22));

            return next.ToOffset(CallingHours.DhakaOffset).Hour == 9 && next.Day == 29;
        });

        check("a new order is rung at once, not in twenty minutes", () =>
            redial.NextAttemptAt(0, null, hours, Dhaka(11)) == Dhaka(11));

        check("a second attempt waits twenty minutes", () =>
            redial.NextAttemptAt(1, Dhaka(11), hours, Dhaka(11, 1)) == Dhaka(11, 20));

        check("the gap widens on the third", () =>
            redial.NextAttemptAt(2, Dhaka(11), hours, Dhaka(11, 30)) == Dhaka(13));

        // Three unanswered calls is diligence; a fourth gets the number blocked.
        check("after the limit it stops rather than ringing again", () =>
            redial.NextAttemptAt(3, Dhaka(11), hours, Dhaka(12)) is null);

        check("a retry that lands at night is pushed to the morning", () =>
        {
            var next = redial.NextAttemptAt(2, Dhaka(20, 30), hours, Dhaka(20, 35));

            return next is { } n && n.ToOffset(CallingHours.DhakaOffset).Hour == 9 && n.Day == 29;
        });

        var script = CallScript.Default;

        check("one confirms, zero rejects, nine asks for a person", () =>
            IvrDecision.Read(script, "1") == CallOutcome.Confirmed
            && IvrDecision.Read(script, "0") == CallOutcome.Rejected
            && IvrDecision.Read(script, "9") == CallOutcome.NeedsHuman);

        // Silence is not consent, and a key this script does not use is not a decision.
        check("silence and an unused key mean nothing", () =>
            IvrDecision.Read(script, null) is null && IvrDecision.Read(script, "5") is null);

        check("running out of prompts sends the order to a person", () =>
            IvrDecision.Exhausted == CallOutcome.NeedsHuman);

        check("a merchant can use their own keys", () =>
        {
            var reminder = new CallScript
            {
                Greeting = "…", Repeat = "…", Confirmed = "…", Rejected = "…", Handover = "…",
                Keys = new Dictionary<string, CallOutcome>(StringComparer.Ordinal)
                {
                    ["5"] = CallOutcome.Confirmed,
                },
            };

            return IvrDecision.Read(reminder, "5") == CallOutcome.Confirmed
                && IvrDecision.Read(reminder, "1") is null;
        });

        var order = new Order
        {
            Reference = "ORD-1041",
            CustomerName = "সাদিয়া আক্তার",
            Msisdn = "8801711223344",
            Amount = 1250m,
            Summary = "দুইটি শার্ট",
        };

        check("the script is filled from the order", () =>
            ScriptRenderer.Render("{দোকান} · {নাম} · {টাকা}", order, "রাফি ফ্যাশন")
                == "রাফি ফ্যাশন · সাদিয়া আক্তার · 1250");

        // What the customer must send, not what the shop charged. Showing Amount here is
        // how a customer sends a figure that matches no invoice.
        check("the spoken figure is what the customer must actually send", () =>
        {
            var salted = new Order
            {
                Reference = "ORD-1", CustomerName = "ক", Msisdn = "8801711223344",
                Amount = 1250m, ChargedAmount = 1251m,
            };

            return ScriptRenderer.Render("{টাকা}", salted, "দোকান") == "1251";
        });

        // A broken script should sound broken, not finished.
        check("an unknown placeholder stays visible", () =>
            ScriptRenderer.Render("তারিখ {কবে}", order, "দোকান") == "তারিখ {কবে}");

        check("an empty summary leaves no double space", () =>
        {
            var bare = new Order
            {
                Reference = "ORD-1", CustomerName = "ক", Msisdn = "8801711223344",
                Amount = 10m, Summary = null,
            };

            return ScriptRenderer.Render("আগে {পণ্য} পরে", bare, "দোকান") == "আগে পরে";
        });

        // A server under a Bangla locale must not emit Bengali digits: a speech model
        // reads those unpredictably.
        check("amounts are always ASCII digits", () =>
        {
            var previous = System.Globalization.CultureInfo.CurrentCulture;

            try
            {
                System.Globalization.CultureInfo.CurrentCulture =
                    new System.Globalization.CultureInfo("bn-BD");

                return ScriptRenderer.Render("{টাকা}", order, "দোকান") == "1250";
            }
            finally
            {
                System.Globalization.CultureInfo.CurrentCulture = previous;
            }
        });
    }
}
