using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Casazen.Core.Options;
using Casazen.Core.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Casazen.Infrastructure.External;

public sealed class WhatsAppCloudSender(HttpClient httpClient, IOptions<WhatsAppOptions> options, ILogger<WhatsAppCloudSender> logger)
    : IWhatsAppSender
{
    public async Task<WhatsAppSendResult> SendTemplateAsync(
        string phone, string template, string language, IReadOnlyList<WhatsAppTemplateParameter> body,
        CancellationToken cancellationToken = default)
    {
        var settings = options.Value;
        if (!settings.Enabled || string.IsNullOrWhiteSpace(settings.PhoneNumberId) || string.IsNullOrWhiteSpace(settings.AccessToken))
            return new WhatsAppSendResult(false);

        var digits = string.Concat(phone.Where(char.IsDigit));
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{settings.ApiVersion}/{settings.PhoneNumberId}/messages");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.AccessToken);
        request.Content = JsonContent.Create(new
        {
            messaging_product = "whatsapp",
            recipient_type = "individual",
            to = digits,
            type = "template",
            template = new
            {
                name = template,
                language = new { code = language },
                components = new[] { new { type = "body", parameters = body.Select(p => new { type = "text", text = p.Value }) } },
            },
        });

        using var response = await httpClient.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            logger.LogWarning("WhatsApp template send rejected with status {StatusCode}", (int)response.StatusCode);
            return new WhatsAppSendResult(false);
        }

        using var json = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(cancellationToken));
        var id = json.RootElement.TryGetProperty("messages", out var messages) && messages.GetArrayLength() > 0
            && messages[0].TryGetProperty("id", out var value) ? value.GetString() : null;
        return new WhatsAppSendResult(!string.IsNullOrWhiteSpace(id), id);
    }
}
