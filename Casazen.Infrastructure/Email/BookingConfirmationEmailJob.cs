using Casazen.Core.Entities;
using Casazen.Core.Services;
using Casazen.Infrastructure.External;
using Hangfire;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Email;

/// <summary>
/// Hangfire job that hands to the provider the confirmation e-mail of a direct booking that carries the check-in link of a session (E23/F02/US01/T01,
/// <see cref="EmailContent.CheckInLink"/>) and writes its real outcome on the session and on the delivery row of the booking, as <see cref="GuestCheckInLinkEmailJob"/> does for the
/// e-mail of the link: the link is «sent» only when the provider accepted the e-mail (CO-09, A5-26: never «sent» when nothing left). The e-mail is the confirmation of the booking
/// too, so it goes out whatever the state of the link.
/// </summary>
/// <remarks>
/// <para><b>Retries.</b> A temporary provider error is tried again by this job, up to <see cref="MaxAttempts"/> attempts, the first one included (the delay of each is in
/// <see cref="RetryDelays"/>); Hangfire retries only an unexpected error and then drops the job, so the e-mail, which holds the link, does not stay in its tables.</para>
/// <para><b>A failure.</b> An e-mail the provider refused, one that no attempt delivered, or one that cannot be sent because the provider is not configured leaves the guest without the
/// link, and nobody else has it (only its hash is stored): the delivery row and the session say <c>Failed</c> with the stable code, and the session is taken back (it expires), so that the
/// daily job <c>guest-checkin-send</c> issues a new link and e-mails it at its next run, with an outcome of its own. Never a second link while the first one may still reach the guest.</para>
/// <para><b>Nothing of the guest in a log:</b> the booking id, the template, the attempt and the code of the failure.</para>
/// </remarks>
public sealed class BookingConfirmationEmailJob(
    IEmailService emailService,
    ICheckInLinkDeliveryService deliveries,
    IBackgroundJobClient backgroundJobClient,
    ILogger<BookingConfirmationEmailJob> logger)
{
    /// <summary>Attempts of one e-mail, the first one included.</summary>
    public const int MaxAttempts = 5;

    /// <summary>Delay before attempt 2, 3, 4 and 5.</summary>
    public static readonly TimeSpan[] RetryDelays =
    [
        TimeSpan.FromMinutes(1),
        TimeSpan.FromMinutes(3),
        TimeSpan.FromMinutes(10),
        TimeSpan.FromMinutes(30),
    ];

    // Provider errors are handled (and retried) here; Hangfire retries only unexpected errors (e.g. database), then drops the job so the link does not stay in its tables.
    [AutomaticRetry(Attempts = 3, OnAttemptsExceeded = AttemptsExceededAction.Delete)]
    public Task SendAsync(string to, string subject, string htmlBody, string template, Guid bookingId, Guid sessionId, int attempt) =>
        DeliverAsync(to, subject, htmlBody, template, bookingId, sessionId, attempt, replyTo: null, fromName: null);

    /// <summary>
    /// The same for the confirmation of a guest that carries the brand of its host (E50/F01/US03/T01, NF-155): sent in the host's name (<paramref name="fromName"/>, the sender's
    /// display name; the address is the configured one) with the host as <paramref name="replyTo"/>, and, when it has to be tried again, scheduled again as this method. A <b>new
    /// method</b>: the signature of <see cref="SendAsync"/> is the one the jobs already in the queue were created with, and never changes.
    /// </summary>
    [AutomaticRetry(Attempts = 3, OnAttemptsExceeded = AttemptsExceededAction.Delete)]
    public Task SendBrandedAsync(
        string to,
        string subject,
        string htmlBody,
        string template,
        Guid bookingId,
        Guid sessionId,
        int attempt,
        string? replyTo,
        string? fromName)
    {
        return DeliverAsync(to, subject, htmlBody, template, bookingId, sessionId, attempt, replyTo, fromName);
    }

    private async Task DeliverAsync(
        string to,
        string subject,
        string htmlBody,
        string template,
        Guid bookingId,
        Guid sessionId,
        int attempt,
        string? replyTo,
        string? fromName)
    {
        var branded = replyTo is not null || fromName is not null;
        EmailSendResult result;
        try
        {
            result = branded
                ? await emailService.SendEmailAsync(to, subject, htmlBody, replyTo, fromName)
                : await emailService.SendEmailAsync(to, subject, htmlBody);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Email {Template} of booking {BookingId} failed on attempt {Attempt}", template, bookingId, attempt);
            result = new EmailSendResult(false, ex.GetType().Name, IsTransient: true);
        }

        if (result.Success)
        {
            logger.LogInformation("Email {Template} of booking {BookingId} delivered to the provider", template, bookingId);
            await RecordAsync(() => deliveries.RecordEmailSentAsync(bookingId, sessionId), bookingId);
            return;
        }

        if (result.Skipped)
        {
            logger.LogWarning("Email {Template} of booking {BookingId} skipped: the email provider is not configured", template, bookingId);
            await RecordAsync(
                () => deliveries.RecordEmailFailedAsync(bookingId, sessionId, GuestCheckInLinkEmailErrors.ProviderNotConfigured),
                bookingId);
            return;
        }

        if (result.IsTransient && attempt < MaxAttempts)
        {
            var delay = RetryDelays[Math.Min(attempt, RetryDelays.Length) - 1];
            if (branded)
            {
                backgroundJobClient.Schedule<BookingConfirmationEmailJob>(
                    job => job.SendBrandedAsync(to, subject, htmlBody, template, bookingId, sessionId, attempt + 1, replyTo, fromName),
                    delay);
            }
            else
            {
                backgroundJobClient.Schedule<BookingConfirmationEmailJob>(
                    job => job.SendAsync(to, subject, htmlBody, template, bookingId, sessionId, attempt + 1),
                    delay);
            }

            logger.LogWarning(
                "Email {Template} of booking {BookingId} not delivered on attempt {Attempt}, retried in {Delay}: {Error}",
                template,
                bookingId,
                attempt,
                delay,
                result.ErrorDetail);
            return;
        }

        logger.LogError(
            "Email {Template} of booking {BookingId} not delivered after {Attempt} attempts: {Error}",
            template,
            bookingId,
            attempt,
            result.ErrorDetail);
        await RecordAsync(
            () => deliveries.RecordEmailFailedAsync(
                bookingId,
                sessionId,
                result.IsTransient ? GuestCheckInLinkEmailErrors.NotDelivered : GuestCheckInLinkEmailErrors.Rejected),
            bookingId);
    }

    /// <summary>
    /// Writes the outcome. Never throws: the e-mail has been handed to the provider (or given up) by now, and a retry of the job would send it again.
    /// </summary>
    private async Task RecordAsync(Func<Task> record, Guid bookingId)
    {
        try
        {
            await record();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "The outcome of the confirmation e-mail of booking {BookingId} could not be recorded on its check-in link", bookingId);
        }
    }
}
