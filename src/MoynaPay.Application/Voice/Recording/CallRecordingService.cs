using System.Text.Json;
using MoynaPay.Application.Abstractions;
using MoynaPay.Domain.Orders;

namespace MoynaPay.Application.Voice.Recording;

public sealed record RecordingArchiveResult(string ObjectStorageKey, long SizeBytes, int DurationSeconds);

public interface IRecordingArchive
{
    Task<RecordingArchiveResult> ArchiveAsync(
        string asteriskRecordingName,
        string objectStorageKey,
        CancellationToken ct = default);
}

public sealed class NoopRecordingArchive : IRecordingArchive
{
    public Task<RecordingArchiveResult> ArchiveAsync(
        string asteriskRecordingName,
        string objectStorageKey,
        CancellationToken ct = default) =>
        Task.FromResult(new RecordingArchiveResult(objectStorageKey, 0, 0));
}

public sealed record RecordingFinishedCommand
{
    public required Guid MerchantId { get; init; }
    public required Guid OrderId { get; init; }
    public required Guid CallSessionId { get; init; }
    public required string RecordingName { get; init; }
    public TimeSpan PlaybackUrlLifetime { get; init; } = TimeSpan.FromMinutes(15);
}

public enum RecordingStoreOutcome
{
    Recorded,
    NotFound,
}

public sealed record RecordingStoreResult(
    RecordingStoreOutcome Outcome,
    Order? Order,
    string? ObjectStorageKey,
    string? PlaybackUrl);

public sealed class CallRecordingService(
    IOrderStore orders,
    IRecordingArchive archive,
    RecordingPlaybackUrlSigner signer,
    IClock clock)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<RecordingStoreResult> StoreFinishedAsync(
        RecordingFinishedCommand command,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        var order = await orders.FindByIdAsync(command.MerchantId, command.OrderId, ct)
            .ConfigureAwait(false);
        if (order is null)
            return new RecordingStoreResult(RecordingStoreOutcome.NotFound, null, null, null);

        var now = clock.UtcNow;
        var key = ObjectKey(command);
        var archived = await archive.ArchiveAsync(command.RecordingName, key, ct)
            .ConfigureAwait(false);
        var playbackUrl = signer.Sign(
            archived.ObjectStorageKey,
            now.Add(command.PlaybackUrlLifetime));

        await orders.SaveAuditAsync(order, new OrderEvent
        {
            TenantId = command.MerchantId,
            OrderId = command.OrderId,
            Type = "voice.recording_stored",
            Actor = Actor.Machine,
            From = order.Status,
            To = order.Status,
            Detail = "Call recording stored.",
            PayloadJson = JsonSerializer.Serialize(new
            {
                callSessionId = command.CallSessionId,
                recordingName = command.RecordingName,
                objectStorageKey = archived.ObjectStorageKey,
                archived.SizeBytes,
                archived.DurationSeconds,
                playbackUrl,
            }, Json),
            At = now,
            CreatedAt = now,
            UpdatedAt = now,
        }, ct).ConfigureAwait(false);

        return new RecordingStoreResult(
            RecordingStoreOutcome.Recorded,
            order,
            archived.ObjectStorageKey,
            playbackUrl);
    }

    public static string RecordingName(Guid callSessionId) =>
        $"moynapay-{callSessionId:N}";

    private static string ObjectKey(RecordingFinishedCommand command) =>
        $"recordings/{command.MerchantId:D}/{command.OrderId:D}/{command.CallSessionId:D}/" +
        $"{SafeName(command.RecordingName)}.wav";

    private static string SafeName(string value)
    {
        var cleaned = new string(value
            .Where(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_')
            .ToArray());

        return string.IsNullOrWhiteSpace(cleaned) ? "recording" : cleaned;
    }
}
