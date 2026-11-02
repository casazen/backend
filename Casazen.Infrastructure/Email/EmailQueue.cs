using Casazen.Infrastructure.External;
using Hangfire;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Casazen.Infrastructure.Email;

/// <summary>A rendered email: plain-text subject and complete HTML document.</summary>
/// <param name="ReplyTo">
/// Where an answer to the email goes, when it is not the sender: the address of a visitor who wrote to the host through the booking
/// site (DB-03b). <c>null</c> for every other email, which is queued exactly as before.
/// </param>
public sealed record EmailContent(string Subject, string HtmlBody, string? ReplyTo = null);

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
            // An email without a reply-to goes through the job it always went through, so the jobs already waiting in the queue during a
            // deploy still find their method; the reply-to has a job of its own.
            var replyTo = string.IsNullOrWhiteSpace(content.ReplyTo) ? null : content.ReplyTo.Trim();
            var jobId = replyTo is null
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

/// <summary>
/// Hangfire job that hands one queued email to <see cref="IEmailService"/>. Transient provider errors are retried;
/// after the last attempt, or on a permanent error, the job is removed so the recipient and body do not stay in the
/// Hangfire tables (each failure is logged).
/// </summary>
public sealed class EmailDeliveryJob(IEmailService emailService, ILogger<EmailDeliveryJob> logger)
{
    public const int MaxAttempts = 5;

    [AutomaticRetry(Attempts = MaxAttempts, OnAttemptsExceeded = AttemptsExceededAction.Delete)]
    public Task SendAsync(string to, string subject, string htmlBody, string template) =>
        DeliverAsync(to, subject, htmlBody, template, replyTo: null);

    /// <summary>The same with an address to answer to (DB-03b): the job of an email that has a reply-to.</summary>
    [AutomaticRetry(Attempts = MaxAttempts, OnAttemptsExceeded = AttemptsExceededAction.Delete)]
    public Task SendWithReplyToAsync(string to, string subject, string htmlBody, string template, string replyTo) =>
        DeliverAsync(to, subject, htmlBody, template, replyTo);

    private async Task DeliverAsync(string to, string subject, string htmlBody, string template, string? replyTo)
    {
        var result = replyTo is null
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
