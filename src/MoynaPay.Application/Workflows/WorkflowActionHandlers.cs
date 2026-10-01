using System.Text.Json;
using System.Text.Json.Serialization;
using MoynaPay.Application.Abstractions;
using MoynaPay.Application.Orders;
using MoynaPay.Application.Payments.Invoicing;
using MoynaPay.Domain.Orders;
using MoynaPay.Domain.Payments;

namespace MoynaPay.Application.Workflows;

public sealed class WorkflowActionHandlers(
    IWorkflowActionStore actions,
    IOrderStore orders,
    IInvoiceStore invoices,
    OrderTransitionService transitions,
    IClock clock)
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
    };

    public Task<WorkflowActionResult> NotifyShopAsync(Guid merchantId, Guid orderId,
        CancellationToken ct = default) =>
        RunAsync(merchantId, orderId, "notify-shop", async (order, now) =>
        {
            var payload = JsonSerializer.Serialize(new
            {
                @event = "order.notification",
                reference = order.Reference,
                status = order.Status,
                at = now,
            }, Json);

            await orders.SaveChangeAsync(order, new OrderEvent
            {
                TenantId = merchantId,
                OrderId = order.Id,
                Type = "action.notify_shop",
                Actor = Actor.Machine,
                To = order.Status,
                At = now,
            }, new OutboxMessage
            {
                TenantId = merchantId,
                OrderId = order.Id,
                EventType = "order.notification",
                PayloadJson = payload,
                NextAttemptAt = now,
                CreatedAt = now,
                UpdatedAt = now,
            }, ct).ConfigureAwait(false);

            return "queued";
        }, ct);

    public Task<WorkflowActionResult> CreateInvoiceAsync(Guid merchantId, Guid orderId,
        CancellationToken ct = default) =>
        RunAsync(merchantId, orderId, "create-invoice", async (order, now) =>
        {
            if (await invoices.FindByOrderRefAsync(merchantId, order.Reference, ct).ConfigureAwait(false) is null)
            {
                var window = InvoiceWindow.Default.Apply(now);
                var takenAmounts = await invoices
                    .ListOpenChargedAmountsAsync(merchantId, now, ct)
                    .ConfigureAwait(false);
                var chargedAmount = AmountAllocator.Allocate(order.Amount, takenAmounts);
                var invoice = new Invoice
                {
                    TenantId = merchantId,
                    OrderRef = order.Reference,
                    Amount = order.Amount,
                    ChargedAmount = chargedAmount ?? order.Amount,
                    Status = InvoiceStatus.AwaitingPayment,
                    ExpiresAt = window.ExpiresAt,
                    GraceUntil = window.GraceUntil,
                    Mode = chargedAmount is null ? MatchingMode.TrxId : MatchingMode.UniqueAmount,
                    CustomerName = order.CustomerName,
                    CustomerMsisdn = order.Msisdn,
                    CallbackUrl = order.CallbackUrl,
                    CreatedAt = now,
                    UpdatedAt = now,
                };

                await invoices.SaveAsync(invoice, ct).ConfigureAwait(false);
                order.ChargedAmount = invoice.ChargedAmount;
                order.UpdatedAt = now;
            }

            await orders.SaveChangeAsync(order, new OrderEvent
            {
                TenantId = merchantId,
                OrderId = order.Id,
                Type = "action.create_invoice",
                Actor = Actor.Machine,
                To = order.Status,
                At = now,
            }, null, ct).ConfigureAwait(false);

            return "invoice-created";
        }, ct);

    public Task<WorkflowActionResult> BookCourierAsync(Guid merchantId, Guid orderId,
        CancellationToken ct = default) =>
        RunAsync(merchantId, orderId, "book-courier", async (order, now) =>
        {
            order.Courier ??= "manual";
            order.TrackingCode ??= $"PENDING-{order.Reference}";

            var moved = await transitions.ApplyAsync(new TransitionCommand
            {
                MerchantId = merchantId,
                OrderId = order.Id,
                To = OrderStatus.Booked,
                By = Actor.Machine,
                EventType = "order.booked",
                Reason = "courier booked",
            }, ct).ConfigureAwait(false);

            return moved.Outcome.ToString();
        }, ct);

    private async Task<WorkflowActionResult> RunAsync(Guid merchantId, Guid orderId, string actionId,
        Func<Order, DateTimeOffset, Task<string>> sideEffect, CancellationToken ct)
    {
        var now = clock.UtcNow;
        var start = await actions.TryStartAsync(merchantId, orderId, actionId, now, ct).ConfigureAwait(false);
        if (start != WorkflowActionStart.Started)
        {
            var action = await actions.FindAsync(merchantId, orderId, actionId, ct).ConfigureAwait(false);
            return new WorkflowActionResult(start, action?.Result);
        }

        var order = await orders.FindByIdAsync(merchantId, orderId, ct).ConfigureAwait(false);
        if (order is null) return new WorkflowActionResult(WorkflowActionStart.AlreadyRunning, "order-not-found");

        var result = await sideEffect(order, now).ConfigureAwait(false);
        await actions.CompleteAsync(merchantId, orderId, actionId, result, now, ct).ConfigureAwait(false);

        return new WorkflowActionResult(WorkflowActionStart.Started, result);
    }
}

public sealed record WorkflowActionResult(WorkflowActionStart Start, string? Result);
