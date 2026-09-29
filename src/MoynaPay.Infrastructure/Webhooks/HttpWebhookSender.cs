using System.Net.Http.Headers;
using System.Text;
using MoynaPay.Application.Abstractions;
using MoynaPay.Application.Outbox;
using MoynaPay.Application.Security;
using MoynaPay.Domain.Merchants;

namespace MoynaPay.Infrastructure.Webhooks;

public sealed class HttpWebhookSender(HttpClient http) : IWebhookSender
{
    public async Task<WebhookSendResult> SendAsync(WebhookEndpoint endpoint, string body,
        string signature, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(endpoint);

        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint.Url)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };

        request.Headers.Add(RequestSignature.SignatureHeader, signature);
        request.Headers.Add(RequestSignature.EventHeader, "webhook.test");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        try
        {
            using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
            var status = (int)response.StatusCode;

            return response.IsSuccessStatusCode
                ? new WebhookSendResult(true, status, null)
                : new WebhookSendResult(false, status, $"HTTP {status}");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            // Described, not quoted. The merchant sees this reason in the API response, and
            // the exact text of a connection failure - "No such host is known (internal-db
            // .corp:5432)" against a refusal against a timeout - tells whoever asked which
            // internal names and ports exist. They get the category; the log gets the rest.
            return new WebhookSendResult(false, null, Describe(ex));
        }
    }

    /// <summary>
    /// One delivery from the outbox.
    ///
    /// The body is sent exactly as it is stored. The MAC is over these bytes, so
    /// deserialising and re-serialising it here - even to tidy it - would produce a
    /// signature the shop cannot reproduce.
    /// </summary>
    public async Task<DeliveryAttempt> DeliverAsync(WebhookDelivery delivery,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(delivery);

        var (signature, eventName, deliveryId) = DeliveryHeaders.Build(delivery);

        using var request = new HttpRequestMessage(HttpMethod.Post, delivery.Url)
        {
            Content = new StringContent(delivery.Body, Encoding.UTF8, "application/json"),
        };

        request.Headers.TryAddWithoutValidation(RequestSignature.SignatureHeader, signature);
        request.Headers.TryAddWithoutValidation(RequestSignature.EventHeader, eventName);
        request.Headers.TryAddWithoutValidation(RequestSignature.DeliveryHeader, deliveryId);

        // A shop that needs longer than this should answer 202 and do its work afterwards.
        // Long enough for a slow host in Dhaka, short enough that one unresponsive merchant
        // cannot hold a dispatcher slot for a minute.
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(DeliveryTimeout);

        try
        {
            using var response = await http
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token)
                .ConfigureAwait(false);

            var code = (int)response.StatusCode;

            return response.IsSuccessStatusCode
                ? DeliveryAttempt.Answered(code)
                : DeliveryAttempt.Answered(code, $"HTTP {code}");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Our deadline, not the host shutting down. Those two are the same exception
            // type, and treating them alike logs a stopped worker as a failed shop.
            return DeliveryAttempt.Unreachable(
                $"no answer within {DeliveryTimeout.TotalSeconds:0}s");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return DeliveryAttempt.Unreachable(Describe(ex));
        }
    }

    public static readonly TimeSpan DeliveryTimeout = TimeSpan.FromSeconds(10);

    private static string Describe(Exception ex) => ex switch
    {
        TaskCanceledException => "the shop did not answer in time",

        HttpRequestException http => http.HttpRequestError switch
        {
            HttpRequestError.NameResolutionError => "the host name could not be resolved",
            HttpRequestError.SecureConnectionError => "the TLS certificate was not accepted",
            _ => "the connection was refused or could not be made",
        },

        _ => "the delivery could not be made",
    };
}
