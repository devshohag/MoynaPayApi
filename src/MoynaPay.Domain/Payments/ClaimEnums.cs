namespace MoynaPay.Domain.Payments;

public enum ClaimState
{
    /// <summary>Waiting for a matching message to arrive from the device.</summary>
    Pending = 0,

    /// <summary>A parsed transaction with this id settled the invoice.</summary>
    Matched = 1,

    /// <summary>No message with this id ever arrived on this wallet.</summary>
    Unverified = 2,

    /// <summary>The id belongs to a transaction that is not this invoice's payment.</summary>
    Rejected = 3,
}
