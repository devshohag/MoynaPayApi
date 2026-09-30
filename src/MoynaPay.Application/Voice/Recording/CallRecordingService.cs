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

    Task<bool> ExistsAsync(string objectStorageKey, CancellationToken ct = default);

    Task<bool> DeleteAsync(string objectStorageKey, CancellationToken ct = default);
}

public sealed class NoopRecordingArchive : IRecordingArchive
{
    public Task<RecordingArchiveResult> ArchiveAsync(
        string asteriskRecordingName,
        string objectStorageKey,
        CancellationToken ct = default) =>
        Task.FromResult(new RecordingArchiveResult(objectStorageKey, 0, 0));

    public Task<bool> ExistsAsync(string objectStorageKey, CancellationToken ct = default) =>
        Task.FromResult(true);

    public Task<bool> DeleteAsync(string objectStorageKey, CancellationToken ct = default) =>
        Task.FromResult(true);
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

public enum RecordingPlaybackOutcome
{
    Authorized,
    Forbidden,
    ExpiredOrInvalid,
    Gone,
}

public sealed record RecordingPlaybackRequest(
    Guid MerchantId,
    string ObjectStorageKey,
    long Expires,
    string? Signature);

public sealed record RecordingPlaybackResult(
    RecordingPlaybackOutcome Outcome,
    string? ObjectStorageKey);

public sealed record RecordingRetentionResult(int Examined, int Deleted);

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

    public async Task<RecordingPlaybackResult> AuthorizePlaybackAsync(
        RecordingPlaybackRequest request,
        CancellationToken ct = default)
    {
        if (!signer.Verify(request.ObjectStorageKey, request.Expires, request.Signature, clock.UtcNow))
        {
            return new RecordingPlaybackResult(RecordingPlaybackOutcome.ExpiredOrInvalid, null);
        }

        if (!BelongsToMerchant(request.ObjectStorageKey, request.MerchantId))
        {
            return new RecordingPlaybackResult(RecordingPlaybackOutcome.Forbidden, null);
        }

        if (!await archive.ExistsAsync(request.ObjectStorageKey, ct).ConfigureAwait(false))
        {
            return new RecordingPlaybackResult(RecordingPlaybackOutcome.Gone, null);
        }

        return new RecordingPlaybackResult(
            RecordingPlaybackOutcome.Authorized,
            request.ObjectStorageKey);
    }

    public async Task<RecordingRetentionResult> SweepExpiredAsync(
        IEnumerable<string> objectStorageKeys,
        DateTimeOffset retainUntil,
        CancellationToken ct = default)
    {
        var examined = 0;
        var deleted = 0;

        if (retainUntil > clock.UtcNow)
            return new RecordingRetentionResult(0, 0);

        foreach (var key in objectStorageKeys)
        {
            examined++;
            if (await archive.DeleteAsync(key, ct).ConfigureAwait(false))
                deleted++;
        }

        return new RecordingRetentionResult(examined, deleted);
    }

    public static string RecordingName(Guid callSessionId) =>
        $"moynapay-{callSessionId:N}";

    private static string ObjectKey(RecordingFinishedCommand command) =>
        $"recordings/{command.MerchantId:D}/{command.OrderId:D}/{command.CallSessionId:D}/" +
        $"{SafeName(command.RecordingName)}.wav";

    private static bool BelongsToMerchant(string objectStorageKey, Guid merchantId) =>
        objectStorageKey.StartsWith(
            $"recordings/{merchantId:D}/",
            StringComparison.Ordinal);

    private static string SafeName(string value)
    {
        var cleaned = new string(value
            .Where(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_')
            .ToArray());

        return string.IsNullOrWhiteSpace(cleaned) ? "recording" : cleaned;
    }
}
