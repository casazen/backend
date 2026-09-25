using Casazen.Core.Utilities;
using Casazen.Infrastructure.Email;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Resend;

namespace Casazen.Infrastructure.External;

/// <summary>
/// Sends email through the Resend HTTP API (HTTPS 443, works on every Railway plan) with the sender configured in
/// <see cref="EmailOptions"/>: the sender is never rewritten. Setup, domain verification (SPF/DKIM) and Railway
/// variables: <c>docs/runbooks/email.md</c>.
/// </summary>
public sealed class ResendEmailService(
    IResend resend,
    IOptions<EmailOptions> options,
    ILogger<ResendEmailService> logger) : IEmailService
{
    public async Task<EmailSendResult> SendEmailAsync(string to, string subject, string htmlContent)
    {
        var settings = options.Value;
        if (!settings.IsConfigured)
        {
            logger.LogWarning(
                "Email not sent: the email provider is not configured (Email__ApiKey, Email__FromAddress). Subject: {Subject}",
                subject);
            return EmailSendResult.NotConfigured();
        }

        try
        {
            var response = await resend.EmailSendAsync(BuildMessage(settings, to, subject, htmlContent));
            if (response.Success)
            {
                logger.LogInformation("Email sent via Resend (id={EmailId})", response.Content);
                return EmailSendResult.Sent();
            }

            return Failure(response.Exception);
        }
        catch (ResendException ex)
        {
            return Failure(ex);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or TimeoutException)
        {
            logger.LogWarning(ex, "Resend API unreachable");
            return new EmailSendResult(false, Truncate(ex.Message), IsTransient: true);
        }
    }

    /// <summary>The message handed to Resend: the configured sender, unchanged.</summary>
    public static EmailMessage BuildMessage(EmailOptions settings, string to, string subject, string htmlContent) => new()
    {
        From = new EmailAddress
        {
            Email = settings.FromAddress!.Trim(),
            DisplayName = string.IsNullOrWhiteSpace(settings.FromName) ? null : settings.FromName.Trim(),
        },
        To = to,
        Subject = subject,
        HtmlBody = htmlContent,
    };

    private EmailSendResult Failure(ResendException? ex)
    {
        if (ex is null)
        {
            logger.LogError("Resend API call failed without details");
            return new EmailSendResult(false, "resend_error", IsTransient: true);
        }

        // The provider message can quote an address (e.g. the account owner's on a 403): only its masked form is
        // logged, and the exception object is not passed to the logger for the same reason (FD-17, A9-36).
        var detail = LogRedaction.MaskEmails(ex.Message);
        logger.LogError(
            "Resend API call failed: {ErrorType} (HTTP {StatusCode}, transient={IsTransient}): {Error}",
            ex.ErrorType,
            (int?)ex.StatusCode,
            ex.IsTransient,
            detail);
        return new EmailSendResult(false, Truncate($"{ex.ErrorType}: {ex.Message}"), IsTransient: ex.IsTransient);
    }

    /// <summary>
    /// The detail travels to the email queue logs and to the Hangfire retry message: bounded, without addresses.
    /// </summary>
    private static string Truncate(string value)
    {
        var masked = LogRedaction.MaskEmails(value);
        return masked.Length > 200 ? masked[..200] : masked;
    }
}
