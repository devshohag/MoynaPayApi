namespace MoynaPay.Application.Payments.Parsing;

/// <summary>The structured reading of one provider message.</summary>
public sealed record ParsedMessage
{
    public required MessageKind Kind { get; init; }
    public required decimal Confidence { get; init; }
    public string? TemplateCode { get; init; }

    public decimal? Amount { get; init; }
    public decimal? Fee { get; init; }
    public decimal? BalanceAfter { get; init; }
    public string? TrxId { get; init; }

    /// <summary>Counterparty number where the message gives one.</summary>
    public string? CounterpartyMsisdn { get; init; }

    /// <summary>
    /// The free-text reference on a send money. Optional, customer-typed, and the one
    /// field in the whole message that a merchant could ask a customer to control.
    /// </summary>
    public string? Reference { get; init; }

    /// <summary>Provider timestamp, converted from Dhaka local time.</summary>
    public DateTimeOffset? OccurredAt { get; init; }

    public string? FailureReason { get; init; }

    public static ParsedMessage Unknown(string reason) => new()
    {
        Kind = MessageKind.Unknown,
        Confidence = 0m,
        FailureReason = reason,
    };
}
