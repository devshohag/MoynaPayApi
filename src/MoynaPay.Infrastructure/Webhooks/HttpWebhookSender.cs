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
            return new WebhookSendResult(false, null, ex.Message);
        }
    }
}
