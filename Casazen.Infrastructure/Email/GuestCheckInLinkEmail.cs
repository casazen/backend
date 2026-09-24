using Casazen.Core.Entities;
using Casazen.Core.Exceptions;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Email.Templates;
using Casazen.Infrastructure.External;
using Casazen.Infrastructure.Services;
using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Casazen.Infrastructure.Email;

/// <summary>
/// Email of a guest check-in link with its real outcome on the session (CO-09, A5-26): the host sees whether the email is
/// queued, handed to the provider or failed (and why), never "sent" when nothing left. Whatever the outcome the link stays
/// valid, so the host can copy it and send it another way.
/// </summary>
public interface IGuestCheckInLinkEmailQueue
{
    /// <summary>
    /// Queues the email of the link <paramref name="token"/> of <paramref name="sessionId"/> to the booking's guest
    /// (<see cref="GuestCheckInLinkEmailJob"/>) and records <see cref="GuestCheckInLinkEmailStatus.Queued"/>, or
    /// <see cref="GuestCheckInLinkEmailStatus.Failed"/> with the reason when it cannot be queued (no address, no provider).
    /// </summary>
    Task<GuestCheckInLinkEmailOutcome> QueueAsync(Guid sessionId, string token, CancellationToken cancellationToken = default);
}

/// <summary>State of a link email after <see cref="IGuestCheckInLinkEmailQueue.QueueAsync"/>; <paramref name="Error"/> only when failed.</summary>
public sealed record GuestCheckInLinkEmailOutcome(GuestCheckInLinkEmailStatus Status, string? Error = null);

public sealed class GuestCheckInLinkEmailQueue(
    AppDbContext db,
    IBackgroundJobClient backgroundJobClient,
    IOptions<EmailOptions> emailOptions,
    ILogger<GuestCheckInLinkEmailQueue> logger,
    TimeProvider? timeProvider = null) : IGuestCheckInLinkEmailQueue
{
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;

    public async Task<GuestCheckInLinkEmailOutcome> QueueAsync(Guid sessionId, string token, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        var session = await db.GuestCheckInSessions
            .Include(s => s.Booking)
                .ThenInclude(b => b.Guest)
            .FirstOrDefaultAsync(s => s.Id == sessionId, cancellationToken)
            ?? throw new NotFoundException($"Guest check-in session {sessionId} not found");

        var error = !emailOptions.Value.IsConfigured
            ? GuestCheckInLinkEmailErrors.ProviderNotConfigured
            : string.IsNullOrWhiteSpace(session.Booking.Guest?.Email)
                ? GuestCheckInLinkEmailErrors.NoRecipient
                : null;

        // Saved before the job is queued: the job may finish before this request and must find "queued" to update.
        await SetAsync(session, error is null ? GuestCheckInLinkEmailStatus.Queued : GuestCheckInLinkEmailStatus.Failed, error, cancellationToken);
        if (error is not null)
        {
            logger.LogWarning(
                "Check-in link email of booking {BookingId} not queued: {Reason}", session.BookingId, error);
            return new GuestCheckInLinkEmailOutcome(GuestCheckInLinkEmailStatus.Failed, error);
        }

        try
        {
            var jobId = backgroundJobClient.Enqueue<GuestCheckInLinkEmailJob>(job => job.SendAsync(sessionId, token, 1));
            logger.LogInformation(
                "Check-in link email of booking {BookingId} queued (job {JobId})", session.BookingId, jobId);
            return new GuestCheckInLinkEmailOutcome(GuestCheckInLinkEmailStatus.Queued);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Check-in link email of booking {BookingId} could not be queued", session.BookingId);
            await SetAsync(session, GuestCheckInLinkEmailStatus.Failed, GuestCheckInLinkEmailErrors.QueueFailed, cancellationToken);
            return new GuestCheckInLinkEmailOutcome(GuestCheckInLinkEmailStatus.Failed, GuestCheckInLinkEmailErrors.QueueFailed);
        }
    }

    private async Task SetAsync(
        GuestCheckInSession session,
        GuestCheckInLinkEmailStatus status,
        string? error,
        CancellationToken cancellationToken)
    {
        session.LinkEmailStatus = status;
        session.LinkEmailError = error;
        session.UpdatedAt = _clock.GetUtcNow().UtcDateTime;
        await db.SaveChangesAsync(cancellationToken);
    }
}

/// <summary>
/// Hangfire job that sends the email of one check-in link and records the outcome on the session. The email is built when
/// it is sent (current guest address, template <see cref="EmailTemplates.GuestCheckInLink"/>), and only while the link is
/// still usable. Temporary provider errors are retried up to <see cref="MaxAttempts"/> times; a refused email is not.
/// </summary>
public sealed class GuestCheckInLinkEmailJob(
    AppDbContext db,
    IEmailService emailService,
    PublicSiteLinks publicSiteLinks,
    IBackgroundJobClient backgroundJobClient,
    ILogger<GuestCheckInLinkEmailJob> logger,
    TimeProvider? timeProvider = null)
{
    /// <summary>Attempts of one email, the first one included.</summary>
    public const int MaxAttempts = 4;

    /// <summary>Delay before attempt 2, 3 and 4.</summary>
    public static readonly TimeSpan[] RetryDelays = [TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(10), TimeSpan.FromHours(1)];

    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;

    // Provider errors are handled (and retried) here; Hangfire retries only unexpected errors (e.g. database), then drops
    // the job so the token does not stay in the Hangfire tables.
    [AutomaticRetry(Attempts = 3, OnAttemptsExceeded = AttemptsExceededAction.Delete)]
    public async Task SendAsync(Guid sessionId, string token, int attempt)
    {
        var session = await db.GuestCheckInSessions
            .Include(s => s.Booking)
                .ThenInclude(b => b.Guest)
            .Include(s => s.Booking)
                .ThenInclude(b => b.Property)
            .FirstOrDefaultAsync(s => s.Id == sessionId);
        if (session is null)
            return;

        var now = _clock.GetUtcNow().UtcDateTime;
        var booking = session.Booking;
        if (!session.IsOpen
            || session.ExpiresAt < now
            || session.TokenHash != GuestCheckInService.HashToken(token)
            || booking.Status is not (BookingStatus.Confirmed or BookingStatus.CheckedIn))
        {
            await FailAsync(session, GuestCheckInLinkEmailErrors.LinkNotUsable, now);
            return;
        }

        if (!publicSiteLinks.IsConfigured)
        {
            await FailAsync(session, GuestCheckInLinkEmailErrors.LinkUnavailable, now);
            return;
        }

        var recipient = booking.Guest?.Email?.Trim();
        if (string.IsNullOrEmpty(recipient))
        {
            await FailAsync(session, GuestCheckInLinkEmailErrors.NoRecipient, now);
            return;
        }

        var content = EmailTemplates.GuestCheckInLink(
            EmailTemplates.DefaultCulture,
            booking.Guest!.FirstName,
            booking.Property.Name,
            booking.CheckInDate,
            publicSiteLinks.GuestCheckIn(token),
            session.ExpiresAt);

        EmailSendResult result;
        try
        {
            result = await emailService.SendEmailAsync(recipient, content.Subject, content.HtmlBody);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Check-in link email of booking {BookingId} failed on attempt {Attempt}", booking.Id, attempt);
            result = new EmailSendResult(false, ex.GetType().Name, IsTransient: true);
        }

        if (result.Success)
        {
            session.LinkEmailStatus = GuestCheckInLinkEmailStatus.Sent;
            session.LinkEmailError = null;
            session.SentAt = now;
            session.UpdatedAt = now;
            await db.SaveChangesAsync();
            logger.LogInformation("Check-in link email of booking {BookingId} handed to the provider", booking.Id);
            return;
        }

        if (result.Skipped)
        {
            await FailAsync(session, GuestCheckInLinkEmailErrors.ProviderNotConfigured, now);
            return;
        }

        if (result.IsTransient && attempt < MaxAttempts)
        {
            var delay = RetryDelays[Math.Min(attempt, RetryDelays.Length) - 1];
            backgroundJobClient.Schedule<GuestCheckInLinkEmailJob>(job => job.SendAsync(sessionId, token, attempt + 1), delay);
            logger.LogWarning(
                "Check-in link email of booking {BookingId} not delivered on attempt {Attempt}, retried in {Delay}: {Error}",
                booking.Id, attempt, delay, result.ErrorDetail);
            return;
        }

        logger.LogError(
            "Check-in link email of booking {BookingId} not delivered after {Attempt} attempts: {Error}",
            booking.Id, attempt, result.ErrorDetail);
        await FailAsync(
            session,
            result.IsTransient ? GuestCheckInLinkEmailErrors.NotDelivered : GuestCheckInLinkEmailErrors.Rejected,
            now);
    }

    private async Task FailAsync(GuestCheckInSession session, string error, DateTime now)
    {
        logger.LogWarning(
            "Check-in link email of booking {BookingId} not sent: {Reason}", session.BookingId, error);
        session.LinkEmailStatus = GuestCheckInLinkEmailStatus.Failed;
        session.LinkEmailError = error;
        session.UpdatedAt = now;
        await db.SaveChangesAsync();
    }
}
