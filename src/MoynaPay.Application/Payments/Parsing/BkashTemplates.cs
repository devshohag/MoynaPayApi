namespace MoynaPay.Application.Payments.Parsing;

/// <summary>
/// The bKash patterns, written against real messages collected from a live account
/// between July and September 2026.
///
/// Read the ordering rule before adding one: templates are tried in order and the first
/// match wins, so the messages that must never be mistaken for a payment are listed
/// first. An OTP message contains the words "PAYMENT" and an amount; a credit pattern
/// that ran before the OTP pattern could plausibly match it, and the cost of that
/// mistake is an order shipped for money that never arrived.
///
/// Known gap: every credit pattern here comes from a personal account. The wording bKash
/// sends to a merchant or retail account when a customer pays it has not been captured
/// yet, and that is the single most important message in this product. It has to come
/// from a real merchant-account handset during the T0A spike before the pilot.
/// </summary>
public static class BkashTemplates
{
    private const string Amount = @"[\d,]+\.\d{2}";
    private const string TrxId = @"[A-Z0-9]{10}";
    // Deliberately validates the ranges rather than matching any four numbers with
    // slashes in them. A message stamped 32/13/2026 99:99 is corrupt or forged, and
    // catching that in the pattern keeps the rule in one place instead of two.
    private const string Stamp =
        @"(?:0[1-9]|[12]\d|3[01])/(?:0[1-9]|1[0-2])/\d{4} (?:[01]\d|2[0-3]):[0-5]\d";

    public static IReadOnlyList<MessageTemplate> All { get; } =
    [
        // ---------------- non-payments, matched first and discarded ----------------

        new("bkash.otp", MessageKind.Otp,
            @"^Do NOT share your OTP or PIN"),

        // "is being reserved" and the "is successful" that follows it carry the SAME
        // transaction id. Recognising the hold explicitly is what stops the pair from
        // looking like one transaction id arriving twice, which the deduplication index
        // would otherwise treat as a replay.
        new("bkash.payment.reserved", MessageKind.PaymentReserved,
            @"^Payment of Tk\s?(?<amount>" + Amount + @") is being reserved for "),

        new("bkash.payment.sent", MessageKind.PaymentSent,
            @"^Payment of Tk\s?(?<amount>" + Amount + @") to .+ is successful\. " +
            @"Balance Tk\s?(?<balance>" + Amount + @")\. " +
            @"TrxID (?<trxId>" + TrxId + @") at (?<at>" + Stamp + @")"),

        new("bkash.cashout", MessageKind.CashOut,
            @"^Cash Out Tk\s?(?<amount>" + Amount + @") to (?<counterparty>01\d{9}) successful\. " +
            @"Fee Tk\s?(?<fee>" + Amount + @")\. Balance Tk\s?(?<balance>" + Amount + @")\. " +
            @"TrxID (?<trxId>" + TrxId + @") at (?<at>" + Stamp + @")"),

        // Multi-line, and the only template where the label is "TrxID:" with a colon.
        new("bkash.bill.paid", MessageKind.BillPaid,
            @"^Bill successfully paid\."),

        // ---------------- credits ----------------

        // The reference is optional and customer-typed. It is the only field in any bKash
        // message a merchant can ask a payer to control, which makes it worth capturing
        // even though nothing depends on it yet.
        new("bkash.p2p.received", MessageKind.P2PReceived,
            @"^You have received Tk\s?(?<amount>" + Amount + @") from (?<counterparty>01\d{9})\." +
            @"(?: Ref (?<reference>[^.]{1,64})\.)? " +
            @"Fee Tk\s?(?<fee>" + Amount + @")\. Balance Tk\s?(?<balance>" + Amount + @")\. " +
            @"TrxID (?<trxId>" + TrxId + @") at (?<at>" + Stamp + @")"),

        new("bkash.cashin", MessageKind.CashIn,
            @"^Cash In Tk\s?(?<amount>" + Amount + @") from (?<counterparty>01\d{9}) successful\. " +
            @"Fee Tk\s?(?<fee>" + Amount + @")\. Balance Tk\s?(?<balance>" + Amount + @")\. " +
            @"TrxID (?<trxId>" + TrxId + @") at (?<at>" + Stamp + @")"),

        new("bkash.bank.deposit", MessageKind.BankDeposit,
            @"^You have received deposit from iBanking of Tk\s?(?<amount>" + Amount + @") " +
            @"from (?<counterparty>[^.]{1,80})\. " +
            @"Fee Tk\s?(?<fee>" + Amount + @")\. Balance Tk\s?(?<balance>" + Amount + @")\. " +
            @"TrxID (?<trxId>" + TrxId + @") at (?<at>" + Stamp + @")"),
    ];

    /// <summary>Messages from any other sender id are dropped before parsing.</summary>
    public static IReadOnlySet<string> SenderIds { get; } =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "bKash", "16247" };
}
