using MoynaPay.Application.Abstractions;
using MoynaPay.Application.Security;
using MoynaPay.Domain.Orders;

namespace MoynaPay.Application.Outbox;

/// <param name="Failed">
/// Messages this pass could not finish - a write that lost a race, a store that was briefly
/// unreachable. Not a failed delivery: those are counted as Retried or Dead. The lease
/// expires and another pass picks them up.
/// </param>
public readonly record struct DispatchSummary(
    int Claimed, int Delivered, int Retried, int Dead, int Failed)
{
    public bool DidNothing => Claimed == 0;
}

/// <summary>
/// One pass of the outbox: take what is due, tell the shops, write down what happened.
///
/// Everything outside this class is a port, so the whole thing - claiming, ordering,
/// signing, the retry ladder, giving up - runs in a test with no network, no database and
/// no waiting. That matters more here than anywhere else in the product: the failure modes
/// are all about time and repetition, and neither is observable in a system you can only
/// exercise by hand.
///
/// A message is claimed under a lease rather than a held transaction. The POST is to
/// somebody else's server and it can take ten seconds or never answer at all; a database
/// transaction spanning that is how a connection pool runs out overnight.
/// </summary>
public sealed class OutboxDispatcher(
    IOutboxStore outbox,
    IOrderStore orders,
    IMerchantStore merchants,
    ISecretProtector protector,
    IWebhookSender sender,
    IClock clock)
{
    public async Task<DispatchSummary> RunOnceAsync(
        string workerId, int max = 50, CancellationToken ct = default)
    {
        var now = clock.UtcNow;

        // A fresh mark for every pass, not one per worker. It is what the rows claimed by
        // THIS pass are read back by, so two passes of the same worker - one still
        // finishing a slow shop - must not answer to the same name.
        var claim = $"{Shorten(workerId)}/{Guid.CreateVersion7():N}";

        var due = await outbox
            .ClaimDueAsync(claim, max, now, now + DeliveryPolicy.Lease, ct)
            .ConfigureAwait(false);

        int delivered = 0, retried = 0, dead = 0, failed = 0;

        foreach (var message in due)
        {
            DeliveryState state;

            try
            {
                state = await DeliverAsync(message, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // One message must not end the pass. The common cause is a write that lost
                // a race - another dispatcher took the row after this lease expired and
                // finished first - and the right answer to that is to leave their result
                // alone and carry on with the rest of the batch.
                failed++;
                continue;
            }

            switch (state)
            {
                case DeliveryState.Delivered: delivered++; break;
                case DeliveryState.Retry: retried++; break;
                case DeliveryState.Dead: dead++; break;
            }
        }

        return new DispatchSummary(due.Count, delivered, retried, dead, failed);
    }

    private async Task<DeliveryState> DeliverAsync(OutboxMessage message, CancellationToken ct)
    {
        // The clock is read again per message rather than once per pass. A pass can span
        // minutes when several shops are slow, and a retry scheduled from a timestamp taken
        // before all of that would come due immediately.
        var now = clock.UtcNow;

        var order = await orders
            .FindByIdAsync(message.TenantId, message.OrderId, ct).ConfigureAwait(false);

        if (order is null)
        {
            return await StopAsync(message, now, "the order no longer exists", ct).ConfigureAwait(false);
        }

        var endpoint = await merchants.WebhookAsync(message.TenantId, ct).ConfigureAwait(false);

        // The secret always comes from the merchant's registered endpoint, even when the
        // order carries its own callback url. An unsigned delivery is one a shop cannot tell
        // from a forged one, so no endpoint means no delivery - never an unsigned one.
        if (endpoint is null || !endpoint.Active)
        {
            return await StopAsync(message, now, "no active webhook endpoint", ct).ConfigureAwait(false);
        }

        var url = order.CallbackUrl ?? endpoint.Url;

        if (WebhookUrl.Refuse(url) is { } refusal)
        {
            return await StopAsync(message, now, refusal, ct).ConfigureAwait(false);
        }

        var secret = protector.Unprotect(endpoint.SecretCipher, endpoint.KeyRingId);
        var timestamp = now.ToUnixTimeSeconds();

        var attempt = await sender.DeliverAsync(
            new WebhookDelivery(
                url, secret, message.EventType, message.Id, message.PayloadJson, timestamp),
            ct).ConfigureAwait(false);

        var plan = DeliveryPolicy.Plan(message.Attempts, attempt, now);

        message.Attempts++;
        message.LastFailureReason = plan.Reason;
        message.ClaimedBy = null;
        message.ClaimedUntil = null;
        message.UpdatedAt = now;

        switch (plan.State)
        {
            case DeliveryState.Delivered:
                message.DeliveredAt = now;
                break;

            case DeliveryState.Retry:
                message.NextAttemptAt = plan.NextAttemptAt!.Value;
                break;

            case DeliveryState.Dead:
                message.IsDead = true;
                break;
        }

        await outbox.FinishAsync(message, ct).ConfigureAwait(false);

        // The merchant-facing record of how their endpoint is doing, which until now was
        // only ever written by the webhook test.
        await merchants.UpdateWebhookDeliveryAsync(
            message.TenantId,
            plan.State == DeliveryState.Delivered ? now : null,
            plan.State == DeliveryState.Delivered ? null : plan.Reason,
            ct).ConfigureAwait(false);

        return plan.State;
    }

    /// <summary>
    /// Gives up before anything was sent - no endpoint, a refused url, a vanished order.
    ///
    /// Attempts is not incremented, because nothing was attempted. A merchant looking at
    /// this row later needs to see that we never knocked, not a count that suggests their
    /// server was asked eight times and said nothing.
    /// </summary>
    private async Task<DeliveryState> StopAsync(
        OutboxMessage message, DateTimeOffset now, string reason, CancellationToken ct)
    {
        message.IsDead = true;
        message.LastFailureReason = reason;
        message.ClaimedBy = null;
        message.ClaimedUntil = null;
        message.UpdatedAt = now;

        await outbox.FinishAsync(message, ct).ConfigureAwait(false);

        return DeliveryState.Dead;
    }

    /// <summary>
    /// Keeps the claim inside the column. A machine name can be long, and a claim silently
    /// truncated by the database is one nothing can be read back by.
    /// </summary>
    private static string Shorten(string workerId) =>
        string.IsNullOrWhiteSpace(workerId) ? "worker"
        : workerId.Length <= 60 ? workerId
        : workerId[..60];
}

/// <summary>
/// The headers every delivery carries, in one place, so the sender and anything that
/// verifies a delivery in a test cannot drift apart.
/// </summary>
public static class DeliveryHeaders
{
    public static (string Signature, string Event, string Delivery) Build(WebhookDelivery delivery)
    {
        ArgumentNullException.ThrowIfNull(delivery);

        return (
            RequestSignature.SignWebhook(delivery.Secret, delivery.Timestamp, delivery.Body),
            delivery.EventType,
            delivery.DeliveryId.ToString());
    }
}
