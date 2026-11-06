using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
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

/// <inheritdoc cref="IVerificationOperatorAlerts" />
/// <remarks>
/// <para><b>Who</b>: <see cref="VerificationOperatorAudience"/>, the users who hold <c>verification.decision</c> and reach the property; the push
/// and the bell resolve the same people at send time (<see cref="PushAudienceKind.VerificationOperators"/>).</para>
/// <para><b>What</b>: an urgent "To do" about the booking with the action <see cref="ActivityAction.VerifyIdentity"/> (the action and the id of
/// the cockpit; one open item per booking, the service is idempotent while it is open), a push and one e-mail per operator, in the language of
/// the org. The texts name the property, the arrival date and the reason code: never the guest, the document or a picture.</para>
/// <para><b>Order</b>: the "To do" is saved first, together with the pending change of the session that went to the operator (same unit of
/// work), then the push and the e-mails are queued (they never throw).</para>
/// </remarks>
public sealed class VerificationOperatorAlerts(
    AppDbContext db,
    IInternalActivityService activities,
    IPushNotificationService pushNotifications,
    IEmailQueue emailQueue,
    PublicSiteLinks links,
    ILogger<VerificationOperatorAlerts> logger) : IVerificationOperatorAlerts
{
    public async Task RaiseAsync(Guid sessionId, string reasonCode, CancellationToken ct)
    {
        if (!VerificationAlertReasons.IsKnown(reasonCode))
            throw new ArgumentException($"Unknown verification alert reason '{reasonCode}'.", nameof(reasonCode));

        // The anonymous guest request has no tenant: the session is found by id and everything after is scoped to its org.
        var target = await db.GuestCheckInSessions
            .AsNoTracking()
            .IgnoreQueryFilters()
            .Where(s => s.Id == sessionId)
            .Select(s => new
            {
                s.OrgId,
                s.BookingId,
                s.Booking.PropertyId,
                PropertyName = s.Booking.Property.Name,
                PropertyOwnerId = s.Booking.Property.OwnerId,
                s.Booking.CheckInDate,
                BookingStatus = s.Booking.Status,
                s.Booking.NoShowAt,
            })
            .FirstOrDefaultAsync(ct);
        if (target is null)
        {
            logger.LogWarning("Verification alert for session {SessionId} not raised: the session does not exist", sessionId);
            return;
        }

        // The one definition of an active stay (AlloggiatiStatusRules): the queue does not list a request, a hold, a cancelled stay or a no-show,
        // so an alert about one would tell the operators of something they cannot open.
        if (!AlloggiatiStatusRules.ActiveBookingStatuses.Contains(target.BookingStatus) || target.NoShowAt is not null)
        {
            logger.LogInformation(
                "Verification alert of booking {BookingId} not raised: the stay is not active ({Status})", target.BookingId, target.BookingStatus);
            return;
        }

        var language = await db.Orgs
            .AsNoTracking()
            .IgnoreQueryFilters()
            .Where(o => o.Id == target.OrgId)
            .Select(o => o.DefaultLanguage)
            .FirstOrDefaultAsync(ct);
        var culture = OrgInvitationEmails.CultureOf(language);

        try
        {
            await activities.CreateAsync(
                new CreateTodoRequest
                {
                    Type = ActivityType.IdentityVerification,
                    Action = ActivityAction.VerifyIdentity,
                    EntityType = InternalActivity.BookingEntityType,
                    EntityId = target.BookingId,
                    Urgency = ActivityUrgency.High,
                    PropertyId = target.PropertyId,
                    Deadline = RomeCalendar.StartOfDayUtc(target.CheckInDate),
                    Reason = EmailTexts.Get("TodoReason_IdentityVerification", culture),
                },
                ct);
        }
        catch (NotFoundException)
        {
            // The property is gone since the session was read: there is no item to raise, the push and the e-mails still go.
            logger.LogWarning(
                "Verification alert of booking {BookingId}: property {PropertyId} not found, no To do raised",
                target.BookingId, target.PropertyId);
        }

        // The caller's change of the session is saved with the item above; when the item already existed nothing was saved yet.
        await db.SaveChangesAsync(ct);

        var push = EmailTemplates.VerificationOperatorAlertPush(culture, target.PropertyName);
        if (!pushNotifications.Enqueue(
                PushDeliveryKeys.VerificationOperatorAlert(sessionId, reasonCode),
                PushAudience.VerificationOperators(target.BookingId),
                new PushNotificationPayload(push.Title, push.Body, PushTypes.VerificationRequired, target.BookingId, PushRoutes.Booking(target.BookingId))))
        {
            logger.LogWarning("Verification alert push of booking {BookingId} not queued", target.BookingId);
        }

        var recipients = await VerificationOperatorAudience
            .UsersToTell(db, target.OrgId, target.PropertyId, target.PropertyOwnerId)
            .Where(u => u.Email != null && u.Email != string.Empty)
            .Select(u => u.Email)
            .ToListAsync(ct);
        var email = EmailTemplates.VerificationOperatorAlert(
            culture,
            target.PropertyName,
            target.CheckInDate,
            reasonCode,
            links.IsConfigured ? links.HostBooking(target.BookingId) : null);

        var queued = 0;
        foreach (var to in recipients.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (emailQueue.Enqueue(to, email, EmailTemplates.Names.VerificationOperatorAlert))
                queued++;
        }

        logger.LogInformation(
            "Identity verification of booking {BookingId} handed to the operators ({Reason}): {Queued} of {Recipients} e-mails queued",
            target.BookingId, reasonCode, queued, recipients.Count);
    }

    public async Task ResolveAsync(Guid orgId, Guid bookingId, CancellationToken ct)
    {
        // The anonymous guest request has no tenant: the org is named in the query.
        var type = nameof(ActivityType.IdentityVerification);
        var open = await db.InternalActivities
            .AsNoTracking()
            .Where(a => a.OrgId == orgId
                        && a.ActivityType == type
                        && a.EntityType == InternalActivity.BookingEntityType
                        && a.EntityId == bookingId
                        && a.CompletedAt == null)
            .Select(a => a.Id)
            .ToListAsync(ct);

        foreach (var id in open)
            await activities.CloseAsync(id, InternalActivity.ClosedBySystem, ct);
    }
}
