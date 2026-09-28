using MoynaPay.Domain.Common;
using MoynaPay.Domain.Payments;

namespace MoynaPay.Domain.Payments;

/// <summary>
/// A message a device saw, stored exactly as received and never edited. Partitioned by
/// month and archived after ninety days.
///
/// DeviceReceivedAt is the trusted event time and the only clock the matcher reads.
/// Using server time instead would mark a perfectly timely payment as expired whenever
/// a phone had been offline and uploaded its backlog late.
///
/// DedupeHash carries a unique index, which is what makes at-least-once delivery from
/// the device safe: the same message uploaded ten times is ingested once.
/// </summary>
public class RawEvent : BaseEntity
{
    public Guid DeviceId { get; set; }
    public EventSource Source { get; set; }

    /// <summary>Operator sender id, checked against a whitelist before anything else.</summary>
    public string SenderId { get; set; } = null!;

    public string Body { get; set; } = null!;

    public DateTimeOffset DeviceReceivedAt { get; set; }
    public DateTimeOffset ServerReceivedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>SHA-256 over device, sender, body and device timestamp.</summary>
    public string DedupeHash { get; set; } = null!;

    public RawEventState State { get; set; } = RawEventState.Received;

    /// <summary>
    /// Why this message is not Parsed, in words, for the person looking at the ops queue.
    ///
    /// A state on its own says a message failed but not what to do about it: "no template
    /// matched" means the operator changed its wording and a template needs writing, while
    /// a database error means the message was fine and the system was not. Those are two
    /// different jobs for two different people, and a row that shows only Unparseable
    /// makes someone go and read a log to tell them apart.
    /// </summary>
    public string? FailureReason { get; set; }

    public Device Device { get; set; } = null!;
}
