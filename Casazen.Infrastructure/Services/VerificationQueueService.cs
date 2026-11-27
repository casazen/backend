using System.Globalization;
using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Events;
using Casazen.Core.Exceptions;
using Casazen.Core.Regulatory;
using Casazen.Core.Services;
using Casazen.Core.Utilities;
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
/// <remarks>
/// E03/F04/US02/T03: the queue also lists the sessions the guest sent to an operator and the refused consents, each with the technical reasons
/// of its last automatic outcome; every decision writes a <see cref="BiometricVerificationLog"/> row (<see cref="VerificationMethod.Operator"/>),
/// closes the "To do" of the booking, and only an approval releases the lock code to the guest (channel <c>operator</c>, then e-mailed). Every
/// read and write goes through the caller's scope (<see cref="SessionsInScope"/>): a booking of a property the caller does not reach is not
/// listed, not decided, not counted.
/// </remarks>
public class VerificationQueueService(
    AppDbContext db,
    IEmailQueue emailQueue,
    PublicSiteLinks links,
    IGuestCheckInService guestCheckInService,
    ILogger<VerificationQueueService> logger,
    TimeProvider? timeProvider = null,
    IAccessCodeService? accessCodes = null,
    IInternalActivityService? activities = null,
    IDomainEventRecorder? domainEvents = null,
    AgencyDomainLinks? agencyLinks = null,
    IGuestEmailBrandingProvider? emailBranding = null) : IVerificationQueueService
{
    /// <summary>The model name of an operator row when no automatic outcome came before it (a refused consent, a request of the guest).</summary>
    internal const string OperatorModelName = "operator";

    /// <summary>The model version of an operator row when no automatic outcome came before it.</summary>
    internal const string OperatorModelVersion = "n/a";

    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;

    /// <summary>
    /// Get bookings needing manual verification within the host's scope (AC-NF12-3).
    /// A booking needs verification when its session went to the operator (E03/F04/US02/T03: a request of the guest, a refused consent, a
    /// review, the attempts used; before it: BiometricOutcome Escalate or 3 attempts) and no operator decided it yet.
    /// </summary>
    public async Task<IReadOnlyList<PendingVerification>> GetPendingVerificationsAsync(
        string userId,
        HostScope scope,
        CancellationToken cancellationToken = default)
    {
        var query = from session in PendingSessions(scope)
                    join booking in db.Bookings on session.BookingId equals booking.Id
                    join property in db.Properties on booking.PropertyId equals property.Id
                    join guest in db.Guests on booking.GuestId equals guest.Id
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
                        SessionCreatedAt = session.CreatedAt,
                    };

        // AC-NF12-3: the sessions are the ones of the bookings of the caller scope (InScope handles the scope shapes)
        var found = await query
            .OrderBy(v => v.CheckInDate)
            .ToListAsync(cancellationToken);

        // One entry for each booking: when a new link replaced the session that handed the verification over, the latest session says it.
        var results = found
            .GroupBy(v => v.Id)
            .Select(g => g.OrderByDescending(v => v.SessionCreatedAt).First())
            .OrderBy(v => v.CheckInDate)
            .ToList();

        var bookingIds = results.Select(r => r.Id).Distinct().ToList();
        var automatic = (await db.BiometricVerificationLogs
                .AsNoTracking()
                .Where(l => bookingIds.Contains(l.BookingId) && l.Method == VerificationMethod.Biometric)
                .Select(l => new { l.BookingId, l.CreatedAt, l.ReasonsJson, l.Provisional })
                .ToListAsync(cancellationToken))
            .GroupBy(l => l.BookingId)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(l => l.CreatedAt).First());

        return results.Select(r =>
        {
            var last = automatic.GetValueOrDefault(r.Id);
            return new PendingVerification(
                r.Id,
                r.PropertyId,
                r.Name,
                r.CheckInDate,
                r.CheckOutDate,
                $"{r.FirstName} {r.LastName}".Trim(),
                r.Email,
                r.BiometricOutcome,
                r.BiometricAttempts,
                r.UpdatedAt,
                VerificationReasons.Parse(last?.ReasonsJson),
                last?.Provisional ?? false);
        }).ToList();
    }

    /// <summary>
    /// Request a new selfie from the guest by sending an email with a check-in link (AC-NF12-5).
    /// Returns false when the booking has no active session or the guest has no email.
    /// </summary>
    public async Task<bool> RequestNewPhotoAsync(
        Guid bookingId,
        HostScope scope,
        string? operatorUserId = null,
        CancellationToken cancellationToken = default)
    {
        var session = await SessionsInScope(scope)
            .Include(s => s.Booking)
                .ThenInclude(b => b.Guest)
            .Include(s => s.Booking)
                .ThenInclude(b => b.Property)
            .Where(s => s.BookingId == bookingId)
            .OrderByDescending(s => s.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken);

        if (session == null)
        {
            logger.LogWarning("No check-in session found in the caller scope for booking {BookingId}", bookingId);
            return false;
        }

        var booking = session.Booking;
        var guest = booking.Guest;
        if (string.IsNullOrWhiteSpace(guest.Email))
        {
            logger.LogWarning("Guest {GuestId} of booking {BookingId} has no email address", guest.Id, bookingId);
            return false;
        }

        if (!links.IsConfigured)
        {
            logger.LogWarning(
                "Cannot send new photo request email for booking {BookingId}: public site URL not configured",
                bookingId);
            return false;
        }

        await using var requestLock = await PostgresAdvisoryLocks.TryAcquireSessionLockAsync(
            db, PostgresAdvisoryLocks.Scope.VerificationPhotoRequest, bookingId.ToString("N"), cancellationToken);
        if (requestLock is null)
            throw new DomainConflictException("verification_photo_request_in_progress", "ConflictDetail");

        // A repeat of a completed request must not invalidate the link already in the guest's mailbox.
        var now = UtcNow();
        if (await VerificationHandOff.AwaitingReplacementPhoto(db, scope.OrgId).ContainsAsync(bookingId, cancellationToken)
            && await db.GuestCheckInSessions.AnyAsync(s => s.BookingId == bookingId && s.ExpiresAt > now
                && (s.Status == GuestCheckInSessionStatus.Inviato || s.Status == GuestCheckInSessionStatus.InCompilazione), cancellationToken))
            return true;

        var pending = await PendingSessions(scope).Where(s => s.BookingId == bookingId)
            .OrderByDescending(s => s.CreatedAt).FirstOrDefaultAsync(cancellationToken);
        var withOperator = pending is not null || await VerificationHandOff.IsWithOperatorAsync(db, bookingId, cancellationToken);

        // Replacing a legacy escalated link must not remove the hand-off if the email queue refuses the request.
        if (pending is { OperatorRequestedAt: null })
        {
            pending.OperatorRequestedAt = pending.CreatedAt;
            await db.SaveChangesAsync(cancellationToken);
        }

        // Hangfire is independent of EF. Commit the usable link before publishing it, and keep the hand-off until queueing succeeds.
        // E40/F01/US01/T03: the link is on the agency's own domain when its guest area is served there, on the CasaZen domain otherwise; read
        // before the link is issued, so nothing waits between the new link and its e-mail.
        var agencyCheckInLinks = await agencyLinks.ForOrgOrPlatformAsync(links, booking.OrgId, cancellationToken);
        var newLink = await guestCheckInService.IssueLinkAsync(bookingId, booking.OrgId);
        var checkInLink = agencyCheckInLinks.GuestCheckIn(newLink.Token);
        var guestName = $"{guest.FirstName} {guest.LastName}".Trim();
        var propertyName = booking.Property.Name;
        var culture = EmailTemplates.DefaultCulture;

        // E50/F01/US03/T01: the brand of the host while the guest portal is on (null = the CasaZen e-mail).
        var branding = await emailBranding.ForOrgOrNullAsync(booking.OrgId, cancellationToken);
        var email = EmailTemplates.GuestNewPhotoRequest(
            culture,
            guestName,
            propertyName,
            booking.CheckInDate,
            checkInLink,
            branding);

        if (!emailQueue.Enqueue(guest.Email, email, EmailTemplates.Names.GuestNewPhotoRequest))
        {
            logger.LogWarning(
                "New photo request email for booking {BookingId} not queued",
                bookingId);
            return false;
        }

        // The email now contains the new link. Finish persisting it even if the HTTP client disconnects.
        if (withOperator)
        {
            await using var transaction = db.Database.IsRelational() ? await db.Database.BeginTransactionAsync(CancellationToken.None) : null;
            await AddReopenAsync(pending ?? session, operatorUserId, CancellationToken.None);
            await CloseTodoAsync(session.OrgId, bookingId, operatorUserId, CancellationToken.None);
            if (transaction is not null)
                await transaction.CommitAsync(CancellationToken.None);
        }

        logger.LogInformation(
            "Requested new photo for booking {BookingId}, new session {SessionId} issued, email queued",
            bookingId, newLink.SessionId);

        return true;
    }

    /// <summary>
    /// The operator proposes a video call (E22/F01/US01/T03): the link of the meeting goes to the guest by e-mail, in the guest's language, and CasaZen keeps it
    /// in no table and no log (it stays in the body of the queued e-mail, the Hangfire job, until that job expires; the service has no HTTP client: the link is
    /// not fetched either). The booking must be one the caller reaches whose verification waits for an operator; the
    /// verification is not decided by this, the operator decides after the call as ever. What is recorded is only that a call was proposed, and when, on the
    /// open "To do" of the verification (its reason and a line of its notes, in the org's language, the id of the operator not in it), and in the log of the
    /// application with the booking and the operator, no link.
    /// </summary>
    public async Task<VideoCallOutcome> ProposeVideoCallAsync(
        Guid bookingId,
        HostScope scope,
        string meetingUrl,
        string? operatorUserId = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);

        var link = VideoCallLinks.TryParse(meetingUrl);
        if (link is null)
            throw new DomainRuleException(VideoCallLinks.UrlInvalidCode, "VerificationVideoCallUrlInvalid", VideoCallLinks.MaxLength);

        var session = await PendingSessions(scope)
            .Include(s => s.Booking)
                .ThenInclude(b => b.Guest)
            .Include(s => s.Booking)
                .ThenInclude(b => b.Property)
            .Where(s => s.BookingId == bookingId)
            .OrderByDescending(s => s.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken);
        if (session == null)
        {
            logger.LogWarning("No pending verification found for booking {BookingId}: no video call proposed", bookingId);
            return VideoCallOutcome.NotPending;
        }

        var booking = session.Booking;
        var guest = booking.Guest;
        if (string.IsNullOrWhiteSpace(guest.Email))
        {
            // Nobody to e-mail: the operator is told, and the "To do" says where else to reach the guest (the chat of the portal the booking came from).
            await UpdateTodoAsync(session.OrgId, bookingId, "TodoReason_IdentityVerificationUseChat", null, null, cancellationToken);
            logger.LogInformation("Video call for booking {BookingId} not sent: the guest has no e-mail address, the portal chat is suggested", bookingId);
            return VideoCallOutcome.GuestUnreachable;
        }

        var culture = GuestLanguages.CultureOf(GuestLanguages.Resolve(session.Language, null));
        // E50/F01/US03/T01: the brand of the host while the guest portal is on (null = the CasaZen e-mail).
        var branding = await emailBranding.ForOrgOrNullAsync(booking.OrgId, cancellationToken);
        var email = EmailTemplates.GuestVideoCall(
            culture,
            $"{guest.FirstName} {guest.LastName}".Trim(),
            booking.Property.Name,
            booking.CheckInDate,
            link.AbsoluteUri,
            branding);
        if (!emailQueue.Enqueue(guest.Email, email, EmailTemplates.Names.GuestVideoCall))
        {
            logger.LogWarning("Video call e-mail of booking {BookingId} not queued", bookingId);
            return VideoCallOutcome.NotSent;
        }

        // Only that a call was proposed, and when, in the words of the org's language: not the link (it is in the e-mail), and not who proposed it (the id of
        // an operator says nothing to the person who reads the "To do"; the log of the application keeps the operator).
        var proposedAt = UtcNow().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
        await UpdateTodoAsync(
            session.OrgId,
            bookingId,
            "TodoReason_IdentityVerificationVideoCall",
            "TodoNote_IdentityVerificationVideoCall",
            proposedAt,
            cancellationToken);
        logger.LogInformation("User {UserId} proposed a video call for booking {BookingId}", operatorUserId ?? "none", bookingId);
        return VideoCallOutcome.Sent;
    }

    /// <summary>
    /// What the open "To do" of the verification of the booking says after a video call was proposed, or could not be (the guest has no e-mail address): its
    /// reason, in the language of the org, and, when there is a <paramref name="noteKey"/>, a line of the same language added to its notes (the newest 2000
    /// characters are kept) with <paramref name="noteValue"/> in it. Nothing when no "To do" is open.
    /// </summary>
    private async Task UpdateTodoAsync(
        Guid orgId,
        Guid bookingId,
        string reasonKey,
        string? noteKey,
        string? noteValue,
        CancellationToken cancellationToken)
    {
        var type = nameof(ActivityType.IdentityVerification);
        var todo = await db.InternalActivities
            .Where(a => a.OrgId == orgId
                        && a.ActivityType == type
                        && a.EntityType == InternalActivity.BookingEntityType
                        && a.EntityId == bookingId
                        && a.CompletedAt == null)
            .OrderBy(a => a.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken);
        if (todo is null)
            return;

        var language = await db.Orgs
            .AsNoTracking()
            .IgnoreQueryFilters()
            .Where(o => o.Id == orgId)
            .Select(o => o.DefaultLanguage)
            .FirstOrDefaultAsync(cancellationToken);
        var culture = OrgInvitationEmails.CultureOf(language);
        todo.Reason = EmailTexts.Get(reasonKey, culture);
        if (noteKey is not null)
        {
            var note = string.Format(culture, EmailTexts.Get(noteKey, culture), noteValue);
            var notes = string.IsNullOrEmpty(todo.Notes) ? note : todo.Notes + "\n" + note;
            todo.Notes = notes.Length > 2000 ? notes[^2000..] : notes;
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// Record manual verification outcome by the operator (AC-NF12-6).
    /// Updates the session with the operator's decision, logs it (E03/F04/US02/T03, AC-NF50-5) and, on an approval only, releases the lock
    /// code to the guest and e-mails it (AC-NF50-4).
    /// </summary>
    public async Task<bool> RecordManualVerificationAsync(
        Guid bookingId,
        HostScope scope,
        bool outcome,
        string? note,
        string? operatorUserId = null,
        CancellationToken cancellationToken = default)
    {
        var session = await PendingSessions(scope)
            .Include(s => s.Booking)
                .ThenInclude(b => b.Property)
            .Include(s => s.Booking)
                .ThenInclude(b => b.Guest)
            .Where(s => s.BookingId == bookingId)
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

        var decided = outcome ? BiometricOutcome.Accept : BiometricOutcome.Reject;
        var now = UtcNow();
        session.BiometricOutcome = decided;
        session.UpdatedAt = now;
        await AddOperatorLogAsync(session, decided, operatorUserId, note, now, cancellationToken);

        // E20/F01/US01/T01: an approval is announced on the bus in the same save as its row of the log (ids and the method, nothing else): an org that
        // lets CasaZen write the guests to the channel manager on their own learns that the identity of the stay is verified. A rejection is not one.
        if (outcome)
        {
            domainEvents?.Record(
                DomainEventType.IdVerificationCompleted,
                bookingId,
                new IdVerificationCompletedPayload(bookingId, session.Booking.PropertyId, IdVerificationMethods.Operator),
                orgId: session.OrgId);
        }

        // The decision first: the gate of the code reads the latest row of the log from the database.
        await db.SaveChangesAsync(cancellationToken);
        await CloseTodoAsync(session.OrgId, bookingId, operatorUserId, cancellationToken);

        logger.LogInformation(
            "Manual verification recorded for booking {BookingId}: outcome={Outcome}",
            bookingId, outcome ? "Accept" : "Reject");

        if (outcome)
            await ReleaseCodeToGuestAsync(session, cancellationToken);

        return true;
    }

    /// <summary>
    /// Reject the guest's check-in with a reason (AC-NF12-7).
    /// Marks the verification as rejected and prevents further attempts; logged as an operator decision (E03/F04/US02/T03). No code is released.
    /// </summary>
    public async Task<bool> RejectVerificationAsync(
        Guid bookingId,
        HostScope scope,
        string reason,
        string? operatorUserId = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            throw new ArgumentException("Rejection reason is required", nameof(reason));
        }

        var session = await PendingSessions(scope)
            .Where(s => s.BookingId == bookingId)
            .OrderByDescending(s => s.CreatedAt)
            .Include(s => s.Booking)
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
        await AddOperatorLogAsync(session, BiometricOutcome.Reject, operatorUserId, reason, now, cancellationToken);

        await db.SaveChangesAsync(cancellationToken);
        await CloseTodoAsync(session.OrgId, bookingId, operatorUserId, cancellationToken);

        // The reason is the operator's free text: it is in the log row, never in the application log.
        logger.LogInformation("Verification rejected for booking {BookingId}", bookingId);

        return true;
    }

    public async Task<VerificationMetrics> GetMetricsAsync(
        HostScope scope,
        DateTime fromUtc,
        DateTime toUtc,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);

        // The bookings the caller reaches, in SQL (InScope): a collaborator «Solo alcuni» never counts the stays of another property.
        var bookings = db.Bookings.InScope(scope);
        var counts = await db.BiometricVerificationLogs
            .AsNoTracking()
            .Where(l => l.OrgId == scope.OrgId
                        && l.CreatedAt >= fromUtc
                        && l.CreatedAt < toUtc
                        && bookings.Any(b => b.Id == l.BookingId))
            .GroupBy(l => new { l.Method, l.Outcome })
            .Select(g => new { g.Key.Method, g.Key.Outcome, Count = g.Count() })
            .ToListAsync(cancellationToken);

        int Count(VerificationMethod method, BiometricOutcome outcome) =>
            counts.Where(c => c.Method == method && c.Outcome == outcome).Sum(c => c.Count);

        var ok = Count(VerificationMethod.Biometric, BiometricOutcome.Accept);
        var review = Count(VerificationMethod.Biometric, BiometricOutcome.Escalate);
        var notOk = Count(VerificationMethod.Biometric, BiometricOutcome.Reject);
        var approved = Count(VerificationMethod.Operator, BiometricOutcome.Accept);
        // Only a rejection is a decision: the operator row that asks the guest for a new photo (Escalate) is not one.
        var rejected = Count(VerificationMethod.Operator, BiometricOutcome.Reject);
        var concluded = ok + approved + rejected;
        var share = concluded == 0 ? 0 : Math.Round((double)(approved + rejected) / concluded, 4);

        return new VerificationMetrics(fromUtc, toUtc, ok, review, notOk, approved, rejected, share);
    }

    /// <summary>
    /// The check-in sessions of the bookings that the caller scope reaches (TN-3). The decision endpoints receive only
    /// a booking id, so this is what stops a collaborator limited to some properties from acting on the guest of another
    /// property of the same org.
    /// </summary>
    private IQueryable<GuestCheckInSession> SessionsInScope(HostScope scope)
    {
        // a query of its own, not InScope inside the lambda: InScope is not a LINQ operator, EF translates this as one EXISTS. The org of the
        // scope is named too: an org-wide scope adds no filter of its own and must not lean on the tenant filter alone.
        var bookings = db.Bookings.InScope(scope);
        return db.GuestCheckInSessions.Where(s => s.OrgId == scope.OrgId && bookings.Any(b => b.Id == s.BookingId));
    }

    /// <summary>
    /// The sessions of the scope that wait for an operator: of an active stay (the one definition of <see cref="AlloggiatiStatusRules"/>: a
    /// request, a hold, a cancelled stay or a no-show is never decided), not accepted, sent to the operator (a request of the guest or the flow, a
    /// refused consent, a review: <b>whether or not a new link has replaced the session since</b>, the hand-off is the booking's, see
    /// <see cref="VerificationHandOff"/>) and with no row of an operator since the hand-off (a decision, or a new photo asked by the operator). The
    /// sessions of before E03/F04/US02 (no consent, no request recorded) keep their rule: a usable session that escalated or used three attempts.
    /// </summary>
    private IQueryable<GuestCheckInSession> PendingSessions(HostScope scope) =>
        SessionsInScope(scope)
            .Where(s => db.Bookings.Any(b => b.Id == s.BookingId && AlloggiatiStatusRules.ActiveBookingStatuses.Contains(b.Status) && b.NoShowAt == null))
            .Unanswered(db);

    /// <summary>
    /// Stages the operator row that opens the way again (the operator asked the guest for a new photo while the booking was with the operator): it
    /// is the latest row, so a rejection no longer holds, it settles the hand-off it answers, and it is <b>not a decision</b> (<c>Escalate</c>: the
    /// gate of the code stays shut, the metrics do not count it). Saved at once, before the guest has a link to use.
    /// </summary>
    private async Task AddReopenAsync(GuestCheckInSession session, string? operatorUserId, CancellationToken cancellationToken)
    {
        db.BiometricVerificationLogs.Add(new BiometricVerificationLog
        {
            OrgId = session.OrgId,
            BookingId = session.BookingId,
            GuestId = session.Booking.GuestId,
            Outcome = BiometricOutcome.Escalate,
            Method = VerificationMethod.Operator,
            Attempts = session.BiometricAttempts,
            ModelName = OperatorModelName,
            ModelVersion = OperatorModelVersion,
            ThresholdsJson = "{}",
            ReasonsJson = "[]",
            Provisional = false,
            OperatorUserId = string.IsNullOrWhiteSpace(operatorUserId) ? null : operatorUserId,
            CreatedAt = UtcNow(),
        });
        await db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// Stages the row of an operator's decision: who, the note, and the model, thresholds, reasons and provisional flag of the automatic outcome
    /// it replaces (none when the guest went to the operator without one). Saved by the caller with the session.
    /// </summary>
    private async Task AddOperatorLogAsync(
        GuestCheckInSession session,
        BiometricOutcome decided,
        string? operatorUserId,
        string? note,
        DateTime now,
        CancellationToken cancellationToken)
    {
        var automatic = await db.BiometricVerificationLogs
            .AsNoTracking()
            .Where(l => l.BookingId == session.BookingId && l.Method == VerificationMethod.Biometric)
            .OrderByDescending(l => l.CreatedAt)
            .Select(l => new { l.ModelName, l.ModelVersion, l.ThresholdsJson, l.ReasonsJson, l.Provisional })
            .FirstOrDefaultAsync(cancellationToken);

        db.BiometricVerificationLogs.Add(new BiometricVerificationLog
        {
            OrgId = session.OrgId,
            BookingId = session.BookingId,
            GuestId = session.Booking.GuestId,
            Outcome = decided,
            Method = VerificationMethod.Operator,
            Attempts = session.BiometricAttempts,
            ModelName = automatic?.ModelName ?? OperatorModelName,
            ModelVersion = automatic?.ModelVersion ?? OperatorModelVersion,
            ThresholdsJson = automatic?.ThresholdsJson ?? "{}",
            ReasonsJson = automatic?.ReasonsJson ?? "[]",
            Provisional = automatic?.Provisional ?? false,
            DurationMs = null,
            OperatorUserId = string.IsNullOrWhiteSpace(operatorUserId) ? null : operatorUserId,
            OperatorNote = string.IsNullOrWhiteSpace(note) ? null : note.Trim(),
            CreatedAt = now,
        });
    }

    /// <summary>Closes the open "To do" of the verification of the booking (<see cref="ActivityType.IdentityVerification"/>): the decision resolves it.</summary>
    private async Task CloseTodoAsync(Guid orgId, Guid bookingId, string? operatorUserId, CancellationToken cancellationToken)
    {
        if (activities is null)
            return;

        var type = nameof(ActivityType.IdentityVerification);
        var open = await db.InternalActivities
            .AsNoTracking()
            .Where(a => a.OrgId == orgId
                        && a.ActivityType == type
                        && a.EntityType == InternalActivity.BookingEntityType
                        && a.EntityId == bookingId
                        && a.CompletedAt == null)
            .Select(a => a.Id)
            .ToListAsync(cancellationToken);

        foreach (var id in open)
            await activities.CloseAsync(id, string.IsNullOrWhiteSpace(operatorUserId) ? InternalActivity.ClosedBySystem : operatorUserId, cancellationToken);
    }

    /// <summary>
    /// After an operator's approval: the lock code through the gate of <see cref="IAccessCodeService"/> (channel <c>operator</c>), e-mailed to the
    /// guest in the language of the check-in page. No code set on the property: nothing is sent.
    /// </summary>
    private async Task ReleaseCodeToGuestAsync(GuestCheckInSession session, CancellationToken cancellationToken)
    {
        if (accessCodes is null)
        {
            logger.LogWarning("Booking {BookingId} approved by an operator: no access code service, no code released", session.BookingId);
            return;
        }

        var code = await accessCodes.ReleaseToGuestAsync(session.BookingId, AccessCodeReleaseChannels.Operator, cancellationToken);
        if (code is null)
        {
            // Held back until the guest pays the tourist tax (E25/F01/US01/T01): nothing is e-mailed; the guest gets the code on the page once it is paid.
            if (await accessCodes.GetHoldReasonAsync(session.BookingId, cancellationToken) is { } hold)
                logger.LogInformation("Booking {BookingId} approved by an operator: the lock code is held back ({Reason}) and not e-mailed", session.BookingId, hold);
            else
                logger.LogInformation("Booking {BookingId} approved by an operator: the property has no lock code to release", session.BookingId);

            return;
        }

        var booking = session.Booking;
        var guest = booking.Guest;
        // E50/F01/US03/T01: the brand of the host while the guest portal is on (null = the CasaZen e-mail).
        var branding = await emailBranding.ForOrgOrNullAsync(session.OrgId, cancellationToken);
        var email = EmailTemplates.GuestAccessCode(
            EmailTemplates.GuestAccessCodeCulture(session.Language),
            $"{guest.FirstName} {guest.LastName}".Trim(),
            booking.Property.Name,
            booking.CheckInDate,
            code,
            branding);
        if (!emailQueue.Enqueue(guest.Email, email, EmailTemplates.Names.GuestAccessCode))
            logger.LogWarning("Access code e-mail of booking {BookingId} not queued after the operator's approval", session.BookingId);
    }

    private DateTime UtcNow() => _clock.GetUtcNow().UtcDateTime;
}
