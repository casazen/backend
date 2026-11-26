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
    public Task<EmailSendResult> SendEmailAsync(string to, string subject, string htmlContent) =>
        SendAsync(to, subject, htmlContent, replyTo: null);

    public Task<EmailSendResult> SendEmailAsync(string to, string subject, string htmlContent, string replyTo) =>
        SendAsync(to, subject, htmlContent, replyTo);

    public Task<EmailSendResult> SendEmailAsync(string to, string subject, string htmlContent, string? replyTo, string? fromName) =>
        SendAsync(to, subject, htmlContent, replyTo, headers: null, fromName);

    public Task<EmailSendResult> SendEmailWithHeadersAsync(
        string to,
        string subject,
        string htmlContent,
        IReadOnlyDictionary<string, string> headers) =>
        SendAsync(to, subject, htmlContent, replyTo: null, headers);

    public Task<EmailSendResult> SendCampaignEmailAsync(string to, string subject, string htmlContent,
        IReadOnlyDictionary<string, string> headers, string hostName, Guid deliveryId) =>
        SendAsync(to, subject, htmlContent, null, headers, hostName, deliveryId);

    private async Task<EmailSendResult> SendAsync(
        string to,
        string subject,
        string htmlContent,
        string? replyTo,
        IReadOnlyDictionary<string, string>? headers = null,
        string? fromName = null, Guid? deliveryId = null)
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
            var message = BuildMessage(settings, to, subject, htmlContent, replyTo, headers, fromName);
            if (deliveryId.HasValue)
                message.Tags = [new EmailTag { Name = "campaign_delivery", Value = deliveryId.Value.ToString("N") }];
            var response = deliveryId is { } key
                ? await resend.EmailSendAsync($"marketing/{key:N}", message)
                : await resend.EmailSendAsync(message);
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

    /// <summary>
    /// The message handed to Resend: the configured sender, unchanged. <paramref name="replyTo"/>, when given, is only the address an
    /// answer goes to (the host's answer to a visitor of the booking site); it never replaces the sender. <paramref name="headers"/>, when
    /// given, are added as they are (the <c>List-Unsubscribe</c> pair of a campaign). <paramref name="fromName"/>, when given, is the name of
    /// the host an email to a guest is sent in (E50/F01/US03/T01, NF-155): it replaces <b>only the display name</b> of the sender, cleaned
    /// (<see cref="GuestEmailBranding.CleanName"/>: no quote, no bracket, no control character, at most 64 characters), and a name that is nothing
    /// after the cleaning leaves the configured one. The <b>address</b> of the sender is always <c>Email:FromAddress</c>.
    /// </summary>
    public static EmailMessage BuildMessage(
        EmailOptions settings,
        string to,
        string subject,
        string htmlContent,
        string? replyTo = null,
        IReadOnlyDictionary<string, string>? headers = null,
        string? fromName = null)
    {
        var message = new EmailMessage
        {
            From = new EmailAddress
            {
                Email = settings.FromAddress!.Trim(),
                DisplayName = GuestEmailBranding.CleanName(fromName)
                    ?? (string.IsNullOrWhiteSpace(settings.FromName) ? null : settings.FromName.Trim()),
            },
            To = to,
            Subject = subject,
            HtmlBody = htmlContent,
        };

        if (!string.IsNullOrWhiteSpace(replyTo))
            message.ReplyTo = replyTo.Trim();

        if (headers is { Count: > 0 })
            message.Headers = new Dictionary<string, string>(headers, StringComparer.OrdinalIgnoreCase);

        return message;
    }

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
