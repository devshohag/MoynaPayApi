using System.Net.Http.Headers;
using System.Text;
using MoynaPay.Application.Abstractions;
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
