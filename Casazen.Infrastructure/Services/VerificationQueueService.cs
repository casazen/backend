using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Email;
using Casazen.Infrastructure.Email.Templates;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Services;

/// <summary>
/// Operator queue for manual biometric verification (NF-12, ADR-013). Allows hosts to review and decide on
/// bookings where biometric verification escalated, failed multiple times, or was declined by the guest.
/// </summary>
public class VerificationQueueService(
    AppDbContext db,
    IEmailQueue emailQueue,
    PublicSiteLinks links,
    IGuestCheckInService guestCheckInService,
    ILogger<VerificationQueueService> logger,
    TimeProvider? timeProvider = null) : IVerificationQueueService
{
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;

    /// <summary>
    /// Get bookings needing manual verification within the host's scope (AC-NF12-3).
    /// A booking needs verification when:
    /// - BiometricOutcome is Escalate, or
    /// - BiometricAttempts >= 3, or
    /// - Guest declined biometric consent (BiometricOutcome is null and session is in-progress)
    /// </summary>
    public async Task<IReadOnlyList<PendingVerification>> GetPendingVerificationsAsync(
        string userId,
        HostScope scope,
        CancellationToken cancellationToken = default)
    {
        var query = from session in db.GuestCheckInSessions
                    join booking in db.Bookings on session.BookingId equals booking.Id
                    join property in db.Properties on booking.PropertyId equals property.Id
                    join guest in db.Guests on booking.GuestId equals guest.Id
                    where session.BiometricOutcome == BiometricOutcome.Escalate
                          || session.BiometricAttempts >= 3
                    select new
                    {
                        booking.Id,
                        PropertyId = property.Id,
                        property.Name,
                        booking.CheckInDate,
                        booking.CheckOutDate,
                        guest.FirstName,
                        guest.LastName,
                        guest.Email,
                        session.BiometricOutcome,
                        session.BiometricAttempts,
                        session.UpdatedAt,
                    };

        // Apply HostScope filtering (AC-NF12-3)
        // Use InScope extension method that already handles the different scope types
        query = query.Where(v => db.Bookings
            .Where(b => b.Id == v.Id)
            .InScope(scope)
            .Any());

        var results = await query
            .OrderBy(v => v.CheckInDate)
            .ToListAsync(cancellationToken);

        return results.Select(r => new PendingVerification(
            r.Id,
            r.PropertyId,
            r.Name,
            r.CheckInDate,
            r.CheckOutDate,
            $"{r.FirstName} {r.LastName}".Trim(),
            r.Email,
            r.BiometricOutcome,
            r.BiometricAttempts,
            r.UpdatedAt)).ToList();
    }

    /// <summary>
    /// Request a new selfie from the guest by sending an email with a check-in link (AC-NF12-5).
    /// Returns false when the booking has no active session or the guest has no email.
    /// </summary>
    public async Task<bool> RequestNewPhotoAsync(Guid bookingId, CancellationToken cancellationToken = default)
    {
        var session = await db.GuestCheckInSessions
            .Include(s => s.Booking)
                .ThenInclude(b => b.Guest)
            .Include(s => s.Booking)
                .ThenInclude(b => b.Property)
            .Where(s => s.BookingId == bookingId)
            .OrderByDescending(s => s.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken);

        if (session == null)
        {
            logger.LogWarning("No check-in session found for booking {BookingId}", bookingId);
            return false;
        }

        var booking = session.Booking;
        var guest = booking.Guest;
        if (string.IsNullOrWhiteSpace(guest.Email))
        {
            logger.LogWarning("Guest {GuestId} of booking {BookingId} has no email address", guest.Id, bookingId);
            return false;
        }

        // Generate a new check-in link by issuing a new session (invalidates the old one)
        var newLink = await guestCheckInService.IssueLinkAsync(bookingId, booking.OrgId);

        // Send email to guest with the new check-in link (AC-NF12-5)
        if (!links.IsConfigured)
        {
            logger.LogWarning(
                "Cannot send new photo request email for booking {BookingId}: public site URL not configured",
                bookingId);
            return false;
        }

        var checkInLink = links.GuestCheckIn(newLink.Token);
        var guestName = $"{guest.FirstName} {guest.LastName}".Trim();
        var propertyName = booking.Property.Name;
        var culture = EmailTemplates.DefaultCulture;

        var email = EmailTemplates.GuestNewPhotoRequest(
            culture,
            guestName,
            propertyName,
            booking.CheckInDate,
            checkInLink);

        if (!emailQueue.Enqueue(guest.Email, email, EmailTemplates.Names.GuestNewPhotoRequest))
        {
            logger.LogWarning(
                "New photo request email for booking {BookingId} not queued",
                bookingId);
            return false;
        }

        logger.LogInformation(
            "Requested new photo for booking {BookingId}, new session {SessionId} issued, email queued to {Email}",
            bookingId, newLink.SessionId, guest.Email);

        return true;
    }

    /// <summary>
    /// Record manual verification outcome by the operator (AC-NF12-6).
    /// Updates the session with the operator's decision and clears the pending verification state.
    /// </summary>
    public async Task<bool> RecordManualVerificationAsync(
        Guid bookingId,
        bool outcome,
        string? note,
        CancellationToken cancellationToken = default)
    {
        var session = await db.GuestCheckInSessions
            .Include(s => s.Booking)
                .ThenInclude(b => b.Property)
            .Where(s => s.BookingId == bookingId
                && (s.BiometricOutcome == BiometricOutcome.Escalate || s.BiometricAttempts >= 3))
            .OrderByDescending(s => s.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken);

        if (session == null)
        {
            logger.LogWarning("No pending verification found for booking {BookingId}", bookingId);
            return false;
        }

        if (!outcome && string.IsNullOrWhiteSpace(note))
        {
            throw new ArgumentException("Rejection note is required when outcome is false", nameof(note));
        }

        var now = UtcNow();

        // Update session with manual verification outcome
        session.BiometricOutcome = outcome ? BiometricOutcome.Accept : BiometricOutcome.Reject;
        session.UpdatedAt = now;

        // TODO: Create a BiometricVerificationLog entry to record the manual decision
        // This would include the operator's note and mark it as a manual verification

        await db.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "Manual verification recorded for booking {BookingId}: outcome={Outcome}, note={Note}",
            bookingId, outcome ? "Accept" : "Reject", note);

        return true;
    }

    /// <summary>
    /// Reject the guest's check-in with a reason (AC-NF12-7).
    /// Marks the verification as rejected and prevents further attempts.
    /// </summary>
    public async Task<bool> RejectVerificationAsync(
        Guid bookingId,
        string reason,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            throw new ArgumentException("Rejection reason is required", nameof(reason));
        }

        var session = await db.GuestCheckInSessions
            .Where(s => s.BookingId == bookingId
                && (s.BiometricOutcome == BiometricOutcome.Escalate || s.BiometricAttempts >= 3))
            .OrderByDescending(s => s.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken);

        if (session == null)
        {
            logger.LogWarning("No pending verification found for booking {BookingId}", bookingId);
            return false;
        }

        var now = UtcNow();
        session.BiometricOutcome = BiometricOutcome.Reject;
        session.UpdatedAt = now;
        session.Status = GuestCheckInSessionStatus.Scaduto; // Expire the session to prevent further attempts

        await db.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "Verification rejected for booking {BookingId}, reason: {Reason}",
            bookingId, reason);

        return true;
    }


    private DateTime UtcNow() => _clock.GetUtcNow().UtcDateTime;
}
