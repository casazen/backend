using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Resend;

namespace Casazen.Infrastructure.External;

/// <summary>
/// Sends email via Resend HTTP API using the official Resend .NET SDK.
/// Works on all Railway plans (HTTPS port 443).
///
/// <para><b>Setup:</b></para>
/// <list type="number">
///   <item>Sign up at https://resend.com (free, no credit card)</item>
///   <item>Create API key → starts with <c>re_</c></item>
///   <item>Set <c>Email__ResendApiKey</c> on Railway</item>
/// </list>
///
/// <para>Free tier: 100 emails/day, 2 emails/second.</para>
/// </summary>
public sealed class ResendEmailService : IEmailService
{
    private readonly string _fromEmail;
    private readonly IResend _resend;
    private readonly ILogger<ResendEmailService> _logger;

    public ResendEmailService(IConfiguration configuration, ILogger<ResendEmailService> logger)
    {
        _fromEmail = configuration["Email:FromAddress"] ?? "onboarding@resend.dev";
        var apiKey = configuration["Email:ResendApiKey"] ?? string.Empty;
        _resend = ResendClient.Create(apiKey);
        _logger = logger;
    }

    public async Task<EmailSendResult> SendEmailAsync(string to, string subject, string htmlContent)
    {
        try
        {
            var resp = await _resend.EmailSendAsync(new EmailMessage
            {
                From = _fromEmail,
                To = to,
                Subject = subject,
                HtmlBody = htmlContent,
            });

            _logger.LogInformation("Email sent to {To} via Resend (id={Id})", to, resp.Content);
            return new EmailSendResult(true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Resend API call failed for {To}", to);
            return new EmailSendResult(false, Truncate(ex.Message, 200));
        }
    }

    private static string Truncate(string value, int max) =>
        value.Length > max ? value[..max] : value;
}
