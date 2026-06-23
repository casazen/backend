using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using MimeKit;
using MimeKit.Text;

namespace Casazen.Infrastructure.External;

/// <summary>
/// MailKit SMTP email sender — drop-in alternative to SendGrid.
/// Configure via Email__Smtp* keys or the standard Email__SendGridApiKey (SendGrid SMTP relay).
///
/// SMTP mode (Email__SmtpHost set):
///   Email__SmtpHost     = smtp.gmail.com
///   Email__SmtpPort     = 587
///   Email__SmtpUsername  = user@gmail.com
///   Email__SmtpPassword  = app-password
///
/// SendGrid SMTP relay (only Email__SendGridApiKey set, no SmtpHost):
///   Uses "apikey" as username + SendGrid API key as password via smtp.sendgrid.net:587.
/// </summary>
public sealed class SmtpEmailService : IEmailService
{
    private readonly string _fromEmail;
    private readonly string? _smtpHost;
    private readonly int _smtpPort;
    private readonly string? _smtpUsername;
    private readonly string? _smtpPassword;
    private readonly string? _sendGridApiKey; // fallback for SendGrid SMTP relay
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
            using var mime = new MimeMessage();
            mime.From.Add(MailboxAddress.Parse(_fromEmail));
            mime.To.Add(MailboxAddress.Parse(to));
            mime.Subject = subject;
            mime.Body = new TextPart(TextFormat.Html) { Text = htmlContent };

            using var client = new SmtpClient();

            var (host, port, username, password) = ResolveCredentials();

            await client.ConnectAsync(host, port, SecureSocketOptions.StartTls);
            await client.AuthenticateAsync(username, password);
            await client.SendAsync(mime);
            await client.DisconnectAsync(true);

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
        // Explicit SMTP config
        if (!string.IsNullOrWhiteSpace(_smtpHost))
        {
            return (
                _smtpHost,
                _smtpPort,
                _smtpUsername ?? string.Empty,
                _smtpPassword ?? string.Empty);
        }

        // SendGrid SMTP relay fallback
        if (!string.IsNullOrWhiteSpace(_sendGridApiKey)
            && !_sendGridApiKey.StartsWith("SG.YOUR", StringComparison.OrdinalIgnoreCase))
        {
            return ("smtp.sendgrid.net", 587, "apikey", _sendGridApiKey);
        }

        throw new InvalidOperationException(
            "No email configuration found. Set Email__SmtpHost (and credentials) for direct SMTP, " +
            "or Email__SendGridApiKey for SendGrid SMTP relay.");
    }

    /// <summary>Strips potentially sensitive SendGrid/SMTP error details.</summary>
    private static string SanitizeError(string raw)
    {
        // Keep the first meaningful line, drop connection strings / stack traces
        var firstLine = raw.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault() ?? "Unknown error";
        return firstLine.Length > 300 ? firstLine[..300] : firstLine;
    }
}
