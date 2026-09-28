namespace MoynaPay.Application.Payments.Parsing;

/// <summary>
/// What a provider message actually says happened.
///
/// Only the credit kinds can ever settle an invoice. The rest are here because the
/// parser has to recognise them in order to throw them away: a message nobody taught it
/// about is a message it might guess at, and a guess that turns an OTP into a payment
/// ships an order nobody paid for.
/// </summary>
public enum MessageKind
{
    /// <summary>No active template matched. Goes to the ops queue - never discarded.</summary>
    Unknown = 0,

    // ---- credits: money arrived in the merchant's wallet ----

    /// <summary>Person-to-person send money. May carry a free-text reference.</summary>
    P2PReceived = 1,

    /// <summary>Cash deposited through an agent.</summary>
    CashIn = 2,

    /// <summary>Add money from a bank's internet banking.</summary>
    BankDeposit = 3,

    /// <summary>Customer paid a merchant account. Merchant-side wording still to be
    /// captured - see BkashTemplates.</summary>
    MerchantPaymentReceived = 4,

    // ---- everything below must never settle an invoice ----

    CashOut = 20,
    PaymentSent = 21,

    /// <summary>The hold placed before a merchant payment completes. Carries the SAME
    /// transaction id as the completion that follows it.</summary>
    PaymentReserved = 22,

    BillPaid = 23,
    Otp = 24,
}

public static class MessageKindExtensions
{
    public static bool IsCredit(this MessageKind kind) => kind is
        MessageKind.P2PReceived or
        MessageKind.CashIn or
        MessageKind.BankDeposit or
        MessageKind.MerchantPaymentReceived;
}
