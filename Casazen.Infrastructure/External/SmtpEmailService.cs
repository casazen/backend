using System.Net;
using System.Net.Mail;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.External;

/// <summary>
/// SMTP email sender via <see cref="System.Net.Mail.SmtpClient"/> — zero external dependencies.
///
/// <para><b>Option A — Direct SMTP:</b></para>
/// <list type="bullet">
///   <item><c>Email__SmtpHost</c>      = smtp.gmail.com</item>
///   <item><c>Email__SmtpPort</c>      = 587</item>
///   <item><c>Email__SmtpUsername</c>   = user@gmail.com</item>
///   <item><c>Email__SmtpPassword</c>   = 16-char app password</item>
/// </list>
///
/// <para><b>Option B — SendGrid SMTP relay:</b></para>
/// <para>Set only <c>Email__SendGridApiKey</c> — connects to smtp.sendgrid.net:587.</para>
/// </summary>
public sealed class SmtpEmailService : IEmailService
{
    private readonly string _fromEmail;
    private readonly string? _smtpHost;
    private readonly int _smtpPort;
    private readonly string? _smtpUsername;
    private readonly string? _smtpPassword;
    private readonly string? _sendGridApiKey;
    private readonly ILogger<SmtpEmailService> _logger;

    public SmtpEmailService(IConfiguration configuration, ILogger<SmtpEmailService> logger)
    {
        _fromEmail = configuration["Email:FromAddress"] ?? "noreply@casazen.app";
        _smtpHost = configuration["Email:SmtpHost"];
        _smtpPort = int.TryParse(configuration["Email:SmtpPort"], out var port) ? port : 587;
        _smtpUsername = configuration["Email:SmtpUsername"];
        _smtpPassword = configuration["Email:SmtpPassword"];
        _sendGridApiKey = configuration["Email:SendGridApiKey"];
        _logger = logger;
    }

    public async Task<EmailSendResult> SendEmailAsync(string to, string subject, string htmlContent)
    {
        try
        {
            using var mail = new MailMessage
            {
                From = new MailAddress(_fromEmail, "CASAZEN"),
                Subject = subject,
                Body = htmlContent,
                IsBodyHtml = true,
            };
            mail.To.Add(to);

            var (host, port, username, password) = ResolveCredentials();

            using var client = new SmtpClient(host, port)
            {
                EnableSsl = true,
                Credentials = new NetworkCredential(username, password),
                Timeout = 15_000,
            };

            await client.SendMailAsync(mail);

            _logger.LogInformation("Email sent to {To} via SMTP ({Host}:{Port})", to, host, port);
            return new EmailSendResult(true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "SMTP email to {To} failed", to);
            return new EmailSendResult(false, SanitizeError(ex.Message));
        }
    }

    private (string Host, int Port, string Username, string Password) ResolveCredentials()
    {
        if (!string.IsNullOrWhiteSpace(_smtpHost))
        {
            return (_smtpHost, _smtpPort, _smtpUsername ?? string.Empty, _smtpPassword ?? string.Empty);
        }

        if (!string.IsNullOrWhiteSpace(_sendGridApiKey)
            && !_sendGridApiKey.StartsWith("SG.YOUR", StringComparison.OrdinalIgnoreCase))
        {
            return ("smtp.sendgrid.net", 587, "apikey", _sendGridApiKey);
        }

        throw new InvalidOperationException(
            "No email configuration found. Set Email__SmtpHost or Email__SendGridApiKey.");
    }

    private static string SanitizeError(string raw)
    {
        var firstLine = raw.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault() ?? "Unknown error";
        return firstLine.Length > 300 ? firstLine[..300] : firstLine;
    }
}
