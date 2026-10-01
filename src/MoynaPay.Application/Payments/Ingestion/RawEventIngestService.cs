using MoynaPay.Application.Abstractions;
using MoynaPay.Domain.Payments;

namespace MoynaPay.Application.Payments.Ingestion;

/// <summary>
/// Takes a batch of observations from one paired handset and stores what is new.
/// Sender and clock refusals happen before persistence, so forged-looking or impossible
/// messages never enter the matcher queue.
/// </summary>
public sealed class RawEventIngestService(IRawEventStore events, IClock clock)
{
    public static readonly TimeSpan MaxClockSkew = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan MaxBacklogAge = TimeSpan.FromDays(7);

    private static readonly HashSet<string> AllowedSenders =
        new(StringComparer.OrdinalIgnoreCase) { "bKash", "16247" };

    public async Task<IngestRawEventsResult> IngestAsync(
        AppDevice device,
        IReadOnlyList<RawDeviceEvent> batch,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(batch);

        var now = clock.UtcNow;
        int accepted = 0, duplicates = 0, rejected = 0;

        foreach (var item in batch)
        {
            if (!IsAcceptable(item, now))
            {
                rejected++;
                continue;
            }

            var sender = item.SenderId!.Trim();
            var body = item.Body!.Trim();
            var hash = DedupeHash.Compute(device.Id, sender, body, item.ReceivedAt);

            var rawEvent = new RawEvent
            {
                Id = Guid.CreateVersion7(),
                MerchantId = device.MerchantId,
                DeviceId = device.Id,
                Source = item.Source ?? EventSource.Notification,
                SenderId = sender,
                Body = body,
                DeviceReceivedAt = item.ReceivedAt.ToUniversalTime(),
                ServerReceivedAt = now,
                DedupeHash = hash,
                State = RawEventState.Received,
            };

            if (await events.AddIfNewAsync(rawEvent, ct).ConfigureAwait(false))
            {
                accepted++;
            }
            else
            {
                duplicates++;
            }
        }

        return new IngestRawEventsResult(accepted, duplicates, rejected);
    }

    public static bool IsAcceptable(RawDeviceEvent item, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(item);

        if (string.IsNullOrWhiteSpace(item.Body) || item.Body.Length > 2000)
        {
            return false;
        }

        if (!AllowedSenders.Contains(item.SenderId?.Trim() ?? ""))
        {
            return false;
        }

        if (item.ReceivedAt > now.Add(MaxClockSkew))
        {
            return false;
        }

        return item.ReceivedAt >= now.Subtract(MaxBacklogAge);
    }
}

public sealed record RawDeviceEvent(
    EventSource? Source,
    string? SenderId,
    string? Body,
    DateTimeOffset ReceivedAt);

public sealed record IngestRawEventsResult(int Accepted, int Duplicates, int Rejected);
