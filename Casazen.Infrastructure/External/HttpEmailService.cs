using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.External;

/// <summary>
/// Sends email via SendGrid HTTP API (HTTPS port 443) — works on Railway
/// and other PaaS that block outbound SMTP.
///
/// <para>Requires: <c>Email__SendGridApiKey</c> set on Railway.</para>
/// <para>SendGrid free tier: 100 emails/day.</para>
/// </summary>
public sealed class HttpEmailService : IEmailService
{
    private readonly HttpClient _http;
    private readonly string _fromEmail;
    private readonly string _apiKey;
    private readonly ILogger<HttpEmailService> _logger;

    public HttpEmailService(IConfiguration configuration, IHttpClientFactory httpClientFactory, ILogger<HttpEmailService> logger)
    {
        _http = httpClientFactory.CreateClient("SendGrid");
        _fromEmail = configuration["Email:FromAddress"] ?? "noreply@casazen.app";
        _apiKey = configuration["Email:SendGridApiKey"] ?? string.Empty;
        _logger = logger;
    }

    public async Task<EmailSendResult> SendEmailAsync(string to, string subject, string htmlContent)
    {
        try
        {
            var payload = new
            {
                personalizations = new[]
                {
                    new { to = new[] { new { email = to } } }
                },
                from = new { email = _fromEmail, name = "CASAZEN" },
                subject,
                content = new[]
                {
                    new { type = "text/html", value = htmlContent }
                },
            };

            using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.sendgrid.com/v3/mail/send")
            {
                Content = JsonContent.Create(payload),
            };
            request.Headers.Add("Authorization", $"Bearer {_apiKey}");

            var response = await _http.SendAsync(request);
            if (response.IsSuccessStatusCode)
            {
                _logger.LogInformation("Email sent to {To} via SendGrid API", to);
                return new EmailSendResult(true);
            }

            var body = await response.Content.ReadAsStringAsync();
            _logger.LogError("SendGrid API error {Status}: {Body}", (int)response.StatusCode, body.Truncate(300));
            return new EmailSendResult(false, $"SendGrid {(int)response.StatusCode}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "SendGrid API call failed for {To}", to);
            return new EmailSendResult(false, ex.Message.Truncate(200));
        }
    }
}

internal static class StringExtensions
{
    public static string Truncate(this string value, int maxLength) =>
        value.Length > maxLength ? value[..maxLength] : value;
}

// Minimal JSON context for SendGrid payload
[JsonSerializable(typeof(object))]
internal partial class SendGridJsonContext : JsonSerializerContext { }
