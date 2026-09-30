using System.Collections.Concurrent;
using MoynaPay.Application.Voice.Media;

namespace MoynaPay.Application.Voice.Ari;

/// <summary>A call we placed, and the order it is about.</summary>
/// <param name="ShopName">
/// Carried here rather than looked up when the call is answered. The greeting says the
/// shop's name in its first sentence, and a database round trip on an answered line is
/// silence the customer hears.
/// </param>
public sealed record OutboundCall(
    Guid CallSessionId, Guid MerchantId, Guid OrderId, string ShopName, string ChannelId,
    DateTimeOffset PlacedAt);

/// <summary>
/// Which call a channel belongs to.
///
/// This is the whole of the outbound correlation problem, and it is worth saying why it is a
/// problem at all. We ask Asterisk to place a call; some time later a StasisStart arrives
/// naming a channel. Nothing in that event says which of our orders it is about, and the two
/// are not even ordered: on a busy trunk the StasisStart for one call can arrive before the
/// HTTP response for another, so "the channel id the last originate returned" is a guess
/// that is wrong under exactly the load where being right matters.
///
/// The fix is to stop asking Asterisk what the channel id is and to tell it instead. ARI lets
/// the caller choose the id on POST /channels, so the channel is registered here BEFORE the
/// originate goes out, and the StasisStart is matched by a value we already knew. There is no
/// window, because there is nothing to wait for.
///
/// The channel variable is still set, and is still read as a fallback: an Asterisk that
/// ignores a supplied channel id would otherwise leave a call that nothing can steer.
/// </summary>
public sealed class CallCorrelator
{
    private readonly ConcurrentDictionary<string, OutboundCall> _byChannel =
        new(StringComparer.Ordinal);

    private readonly ConcurrentDictionary<Guid, OutboundCall> _bySession = new();

    private readonly ConcurrentDictionary<string, PendingMedia> _mediaByTransport =
        new(StringComparer.Ordinal);

    /// <summary>Channels we have placed and not yet finished with.</summary>
    public int Count => _byChannel.Count;

    /// <summary>
    /// Claims a channel id for a call we are about to place. The caller passes the returned
    /// id to Asterisk.
    ///
    /// Register first, originate second, always. The other order leaves a window in which a
    /// StasisStart arrives for a call nothing knows about yet - and an unrecognised channel
    /// is a customer listening to silence while we work out who they are.
    /// </summary>
    public string Register(Guid callSessionId, Guid merchantId, Guid orderId, string shopName,
        DateTimeOffset placedAt, string? channelId = null)
    {
        var id = channelId ?? $"moynapay-{Guid.CreateVersion7():N}";

        var call = new OutboundCall(callSessionId, merchantId, orderId, shopName, id, placedAt);

        if (!_byChannel.TryAdd(id, call))
        {
            throw new InvalidOperationException($"Channel id '{id}' is already registered.");
        }

        _bySession[callSessionId] = call;

        return id;
    }

    public bool TryGetByChannel(string? channelId, out OutboundCall call)
    {
        call = null!;

        return !string.IsNullOrEmpty(channelId) && _byChannel.TryGetValue(channelId, out call!);
    }

    public bool TryGetBySession(Guid callSessionId, out OutboundCall call) =>
        _bySession.TryGetValue(callSessionId, out call!);

    public string ExpectMedia(Guid callSessionId)
    {
        if (!_bySession.TryGetValue(callSessionId, out var call))
        {
            throw new InvalidOperationException($"Call session '{callSessionId}' is not registered.");
        }

        var transportId = callSessionId.ToString("D");
        _mediaByTransport[transportId] = new PendingMedia(call);

        return transportId;
    }

    public bool TryAttachMedia(IAudioTransport transport, out OutboundCall call)
    {
        ArgumentNullException.ThrowIfNull(transport);
        call = null!;

        if (!_mediaByTransport.TryRemove(transport.TransportId, out var pending))
            return false;

        call = pending.Call;
        pending.Attached.TrySetResult(transport);

        return true;
    }

    public async Task<IAudioTransport?> WaitForMediaAsync(
        Guid callSessionId,
        TimeSpan timeout,
        CancellationToken ct = default)
    {
        var transportId = callSessionId.ToString("D");
        if (!_mediaByTransport.TryGetValue(transportId, out var pending))
            return null;

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var delay = Task.Delay(timeout, timeoutCts.Token);
        var completed = await Task.WhenAny(pending.Attached.Task, delay).ConfigureAwait(false);

        if (completed == pending.Attached.Task)
        {
            await timeoutCts.CancelAsync().ConfigureAwait(false);
            return await pending.Attached.Task.ConfigureAwait(false);
        }

        _mediaByTransport.TryRemove(transportId, out _);
        return null;
    }

    /// <summary>
    /// Attaches a channel that arrived without a registration - the fallback path, where the
    /// session id came from the channel variable instead.
    ///
    /// Returns false when that session is not one of ours either, which is how an inbound
    /// call or somebody else's Stasis application is told apart from a call we placed.
    /// </summary>
    public bool TryAdopt(string channelId, Guid callSessionId, out OutboundCall call)
    {
        call = null!;

        if (string.IsNullOrEmpty(channelId)) return false;
        if (!_bySession.TryGetValue(callSessionId, out var known)) return false;

        call = known with { ChannelId = channelId };

        _byChannel[channelId] = call;
        _bySession[callSessionId] = call;

        return true;
    }

    /// <summary>
    /// Forgets a call. Returns what was forgotten, so the caller can write the outcome down -
    /// this class decides nothing about the call, it only remembers whose it is.
    /// </summary>
    public OutboundCall? Release(Guid callSessionId)
    {
        if (!_bySession.TryRemove(callSessionId, out var call)) return null;

        _byChannel.TryRemove(call.ChannelId, out _);

        return call;
    }

    public OutboundCall? ReleaseByChannel(string? channelId) =>
        TryGetByChannel(channelId, out var call) ? Release(call.CallSessionId) : null;

    /// <summary>
    /// Drops calls that were placed and never seen again.
    ///
    /// A registration is made before the originate, so an originate that fails outright -
    /// trunk down, number refused - leaves an entry nothing will ever release. Without a
    /// sweep those accumulate for the life of the process, and the count that tells an
    /// operator how many calls are live slowly becomes a lie.
    /// </summary>
    public IReadOnlyList<OutboundCall> Sweep(DateTimeOffset now, TimeSpan olderThan)
    {
        var cutoff = now - olderThan;
        var dropped = new List<OutboundCall>();

        foreach (var call in _byChannel.Values)
        {
            if (call.PlacedAt > cutoff) continue;

            if (Release(call.CallSessionId) is { } removed) dropped.Add(removed);
        }

        return dropped;
    }

    private sealed record PendingMedia(OutboundCall Call)
    {
        public TaskCompletionSource<IAudioTransport> Attached { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
