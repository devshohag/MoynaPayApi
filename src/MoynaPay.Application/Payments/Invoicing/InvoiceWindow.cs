using MoynaPay.Domain.Payments;

namespace MoynaPay.Application.Payments.Invoicing;

/// <summary>
/// How long a customer has to pay, and how long after that a payment still counts.
///
/// The grace period is not politeness. Operator messages arrive minutes late, and a
/// phone that lost connectivity uploads its backlog hours late; without grace, payments
/// that were made on time would be rejected because the message describing them was
/// slow. The window is what the customer sees. The grace is what protects them from our
/// plumbing.
/// </summary>
public sealed record InvoiceWindow
{
    public static readonly InvoiceWindow Default = new()
    {
        PayWindow = TimeSpan.FromMinutes(10),
        Grace = TimeSpan.FromMinutes(15),
    };

    public required TimeSpan PayWindow { get; init; }
    public required TimeSpan Grace { get; init; }

    public (DateTimeOffset ExpiresAt, DateTimeOffset GraceUntil) Apply(DateTimeOffset openedAt)
    {
        var expiresAt = openedAt + PayWindow;
        return (expiresAt, expiresAt + Grace);
    }

    /// <summary>
    /// Whether an event that the device says happened at <paramref name="eventTime"/> is
    /// still eligible. Deliberately takes the event time, not the current time: a
    /// backlog uploaded late must be judged by when the payment happened.
    /// </summary>
    public bool Accepts(Invoice invoice, DateTimeOffset eventTime)
    {
        ArgumentNullException.ThrowIfNull(invoice);

        var limit = invoice.GraceUntil == default
            ? invoice.CreatedAt + PayWindow + Grace
            : invoice.GraceUntil;

        return eventTime >= invoice.CreatedAt && eventTime <= limit;
    }
}
