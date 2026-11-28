using Casazen.Infrastructure.Email.Templates;
using Casazen.Infrastructure.External;
using Hangfire;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Casazen.Infrastructure.Email;

public sealed record EmailContent(string Subject, string HtmlBody, string? ReplyTo = null)
{
    public AccountInvitationEmail? AccountInvitation { get; init; }

    /// <summary>
    /// The name the email is sent in, as the sender's display name (E50/F01/US03/T01, NF-155, D3): the name of the host, for the emails to a guest that carry the host's brand
    /// (<see cref="Templates.EmailHtmlBuilder"/> with a <see cref="GuestEmailBranding"/>). Already cleaned: it holds no quote, no bracket, no control character and at most
    /// <see cref="GuestEmailBranding.NameMaxLength"/> characters. Only the display name changes, never the address of the sender. <c>null</c> for every other email, which is queued
    /// exactly as before.
    /// </summary>
    public string? FromName { get; init; }

    public CheckInLinkEmail? CheckInLink { get; init; }
}

public sealed record CheckInLinkEmail(Guid BookingId, Guid SessionId);

/// <summary>
/// Queues emails for delivery in a Hangfire job (<see cref="EmailDeliveryJob"/>), so the provider is never called
/// inside a request: a slow or failing provider cannot turn an already saved operation into an error (A4-20).
/// </summary>
public interface IEmailQueue
{
    /// <summary>
    /// Queues <paramref name="content"/> for <paramref name="to"/>. Never throws: returns false, with a log entry,
    /// when the email is skipped (provider not configured, no recipient) or cannot be queued.
    /// </summary>
    /// <param name="template">Template name, for logs only.</param>
    bool Enqueue(string? to, EmailContent content, string template);
}

public sealed class HangfireEmailQueue(
    IBackgroundJobClient backgroundJobClient,
    IOptions<EmailOptions> options,
    ILogger<HangfireEmailQueue> logger) : IEmailQueue
{
    public bool Enqueue(string? to, EmailContent content, string template)
    {
        if (!options.Value.IsConfigured)
        {
            logger.LogWarning(
                "Email {Template} skipped: the email provider is not configured (Email__ApiKey, Email__FromAddress)",
                template);
            return false;
        }

        if (string.IsNullOrWhiteSpace(to))
        {
            logger.LogWarning("Email {Template} skipped: no recipient address", template);
            return false;
        }

        try
        {
            var replyTo = string.IsNullOrWhiteSpace(content.ReplyTo) ? null : content.ReplyTo.Trim();
            var fromName = GuestEmailBranding.CleanName(content.FromName);
            var jobId = content.CheckInLink is { } checkInLink
                ? fromName is null && replyTo is null
                    ? backgroundJobClient.Enqueue<BookingConfirmationEmailJob>(job =>
                        job.SendAsync(to.Trim(), content.Subject, content.HtmlBody, template, checkInLink.BookingId, checkInLink.SessionId, 1))
                    : backgroundJobClient.Enqueue<BookingConfirmationEmailJob>(job =>
                        job.SendBrandedAsync(
                            to.Trim(), content.Subject, content.HtmlBody, template, checkInLink.BookingId, checkInLink.SessionId, 1, replyTo, fromName))
                : content.AccountInvitation is { } invitation
                    ? backgroundJobClient.Enqueue<EmailDeliveryJob>(job =>
                        job.SendAccountInvitationAsync(to.Trim(), content.Subject, content.HtmlBody, template, invitation, CancellationToken.None))
                    : fromName is not null
                        ? backgroundJobClient.Enqueue<EmailDeliveryJob>(job =>
                            job.SendBrandedAsync(to.Trim(), content.Subject, content.HtmlBody, template, replyTo, fromName))
                        : replyTo is null
                            ? backgroundJobClient.Enqueue<EmailDeliveryJob>(job =>
                                job.SendAsync(to.Trim(), content.Subject, content.HtmlBody, template))
                            : backgroundJobClient.Enqueue<EmailDeliveryJob>(job =>
                                job.SendWithReplyToAsync(to.Trim(), content.Subject, content.HtmlBody, template, replyTo));
            logger.LogInformation("Email {Template} queued (job {JobId})", template, jobId);
            return true;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Email {Template} could not be queued", template);
            return false;
        }
    }
}

public sealed class EmailDeliveryJob(
    IEmailService emailService,
    ILogger<EmailDeliveryJob> logger,
    Auth0InvitationLinks? invitationLinks = null)
{
    public const int MaxAttempts = 5;

    [AutomaticRetry(Attempts = MaxAttempts, OnAttemptsExceeded = AttemptsExceededAction.Delete)]
    public Task SendAsync(string to, string subject, string htmlBody, string template) =>
        DeliverAsync(to, subject, htmlBody, template, replyTo: null);

    [AutomaticRetry(Attempts = MaxAttempts, OnAttemptsExceeded = AttemptsExceededAction.Delete)]
    public Task SendWithReplyToAsync(string to, string subject, string htmlBody, string template, string replyTo) =>
        DeliverAsync(to, subject, htmlBody, template, replyTo);

    /// <summary>
    /// The same in the name of a host (E50/F01/US03/T01, NF-155): the sender's display name is <paramref name="fromName"/> and, when there is one, the answer goes to
    /// <paramref name="replyTo"/>; the address of the sender is the configured one. The job of an email to a guest with the brand of its host. A <b>new method</b>: the signature of the
    /// others is the one the jobs already in the queue were created with, and never changes.
    /// </summary>
    [AutomaticRetry(Attempts = MaxAttempts, OnAttemptsExceeded = AttemptsExceededAction.Delete)]
    public Task SendBrandedAsync(string to, string subject, string htmlBody, string template, string? replyTo, string fromName) =>
        DeliverAsync(to, subject, htmlBody, template, replyTo, fromName);

    [AutomaticRetry(Attempts = MaxAttempts, OnAttemptsExceeded = AttemptsExceededAction.Delete)]
    public async Task SendAccountInvitationAsync(
        string to, string subject, string htmlBody, string template, AccountInvitationEmail invitation, CancellationToken cancellationToken)
    {
        if (invitationLinks?.Enabled == true)
        {
            var original = EmailHtmlBuilder.Encode(invitation.Url);
            if (!htmlBody.Contains(original, StringComparison.Ordinal))
                throw new InvalidOperationException("The invitation link is missing from the email.");

            var link = await invitationLinks.CreateAsync(to, invitation, cancellationToken);
            if (link is null)
            {
                logger.LogInformation("Email {Template} skipped: invitation expired", template);
                return;
            }

            htmlBody = htmlBody.Replace(original, EmailHtmlBuilder.Encode(link), StringComparison.Ordinal);
        }

        await DeliverAsync(to, subject, htmlBody, template, replyTo: null);
    }

    private async Task DeliverAsync(string to, string subject, string htmlBody, string template, string? replyTo, string? fromName = null)
    {
        var result = fromName is not null
            ? await emailService.SendEmailAsync(to, subject, htmlBody, replyTo, fromName)
            : replyTo is null
                ? await emailService.SendEmailAsync(to, subject, htmlBody)
                : await emailService.SendEmailAsync(to, subject, htmlBody, replyTo);
        if (result.Success)
        {
            logger.LogInformation("Email {Template} delivered to the provider", template);
            return;
        }

        if (result.Skipped)
        {
            logger.LogWarning("Email {Template} skipped: the email provider is not configured", template);
            return;
        }

        if (!result.IsTransient)
        {
            logger.LogError("Email {Template} rejected by the provider, not retried: {Error}", template, result.ErrorDetail);
            return;
        }

        logger.LogWarning("Email {Template} not delivered, will be retried: {Error}", template, result.ErrorDetail);
        throw new EmailDeliveryException($"Email {template} not delivered: {result.ErrorDetail}");
    }
}

/// <summary>Transient delivery failure: rethrown so Hangfire retries the job.</summary>
public sealed class EmailDeliveryException(string message) : Exception(message);
