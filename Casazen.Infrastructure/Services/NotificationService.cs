using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Email;
using Casazen.Infrastructure.Email.Templates;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Services;

public class NotificationService(
    AppDbContext db,
    IEmailQueue emailQueue,
    IPushNotificationService pushNotifications,
    PublicSiteLinks links,
    ILogger<NotificationService> logger) : INotificationService
{
    public async Task SendStayAlertAsync(StayAlert alert, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(alert);

        var booking = await db.Bookings
            .AsNoTracking()
            .Include(b => b.Org)
            .Include(b => b.Property)
            .Include(b => b.Guest)
            .FirstOrDefaultAsync(b => b.Id == alert.BookingId, cancellationToken);

        if (booking is null)
        {
            logger.LogWarning("Stay alert {Kind} skipped because booking {BookingId} was not found", alert.Kind, alert.BookingId);
            return;
        }

        var culture = EmailTemplates.DefaultCulture;
        var guestName = $"{booking.Guest.FirstName} {booking.Guest.LastName}".Trim();
        var propertyName = booking.Property.Name;
        var bookingUrl = links.IsConfigured ? links.HostBooking(booking.Id) : null;
        var (template, email) = alert.Kind switch
        {
            StayAlertKind.GuestDataMissing => (
                EmailTemplates.Names.GuestCheckInIncomplete,
                EmailTemplates.GuestCheckInIncomplete(culture, guestName, propertyName, booking.CheckInDate, bookingUrl)),
            StayAlertKind.AlloggiatiDeadlineApproaching => (
                EmailTemplates.Names.AlloggiatiDeadline,
                EmailTemplates.AlloggiatiDeadline(
                    culture, guestName, propertyName, booking.CheckInDate, alert.ShortStay, alert.DeadlineUtc, bookingUrl)),
            StayAlertKind.AlloggiatiOverdue => (
                EmailTemplates.Names.AlloggiatiOverdue,
                EmailTemplates.AlloggiatiOverdue(
                    culture, guestName, propertyName, booking.CheckInDate, alert.DeadlineUtc, alert.ReminderNumber, alert.MaxReminders, bookingUrl)),
            StayAlertKind.AlloggiatiFailed => (
                EmailTemplates.Names.AlloggiatiFailed,
                EmailTemplates.AlloggiatiFailed(culture, guestName, propertyName, booking.CheckInDate, bookingUrl)),
            StayAlertKind.CheckoutReminder => (
                EmailTemplates.Names.CheckoutReminder,
                EmailTemplates.CheckoutReminder(culture, guestName, propertyName, booking.CheckOutDate, bookingUrl)),

            StayAlertKind.ReceiptMissing => (
                EmailTemplates.Names.ReceiptMissing,
                EmailTemplates.ReceiptMissing(culture, propertyName, booking.CheckInDate, bookingUrl)),
            StayAlertKind.ShortStayAtRisk => (
                EmailTemplates.Names.ShortStayAtRisk,
                EmailTemplates.ShortStayAtRisk(culture, propertyName, booking.CheckInDate, alert.DeadlineUtc, bookingUrl)),
            StayAlertKind.OverThirtyNights => (
                EmailTemplates.Names.OverThirtyNights,
                EmailTemplates.OverThirtyNights(culture, propertyName, booking.CheckInDate, bookingUrl)),
            _ => throw new ArgumentOutOfRangeException(nameof(alert), alert.Kind, "Unknown stay alert"),
        };

        // Delivered by EmailDeliveryJob (retries on transient provider errors). A missing contact address or provider
        // is logged by the queue; the push still goes out.
        if (!emailQueue.Enqueue(booking.Org?.ContactEmail, email, template))
        {
            logger.LogWarning(
                "Stay alert {Kind} email of booking {BookingId} not queued (org {OrgId})",
                alert.Kind,
                booking.Id,
                booking.OrgId);
        }

        var isCheckoutReminder = alert.Kind == StayAlertKind.CheckoutReminder;
        var push = isCheckoutReminder
            ? EmailTemplates.CheckoutReminderPush(culture, propertyName)
            : EmailTemplates.StayAlertPush(culture, alert.Kind, propertyName, booking.CheckInDate);
        var deliveryKey = PushDeliveryKeys.StayAlert(
            booking.Id,
            alert.Kind,
            isCheckoutReminder ? booking.CheckOutDate : booking.CheckInDate,
            alert.ReminderNumber);
        var route = isCheckoutReminder ? PushRoutes.BookingCheckout(booking.Id) : PushRoutes.Booking(booking.Id);
        if (!pushNotifications.Enqueue(
                deliveryKey,
                PushAudience.BookingHosts(booking.Id),
                new PushNotificationPayload(push.Title, push.Body, PushTypes.ForStayAlert(alert.Kind), booking.Id, route)))
        {
            logger.LogWarning(
                "Stay alert {Kind} push of booking {BookingId} not queued (org {OrgId})",
                alert.Kind,
                booking.Id,
                booking.OrgId);
        }
    }

    public async Task SendComplianceAlertAsync(
        ComplianceAlert alert,
        IReadOnlyCollection<string> emails,
        string deliveryKey,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(alert);
        ArgumentNullException.ThrowIfNull(emails);
        ArgumentException.ThrowIfNullOrWhiteSpace(deliveryKey);

        var property = await db.Properties
            .AsNoTracking()
            .Where(p => p.Id == alert.PropertyId)
            .Select(p => new { p.Name, p.OrgId })
            .FirstOrDefaultAsync(cancellationToken);
        if (property is null)
        {
            logger.LogWarning("Compliance alert {Type} skipped because property {PropertyId} was not found", alert.Type, alert.PropertyId);
            return;
        }

        var culture = EmailTemplates.DefaultCulture;
        var complianceUrl = links.IsConfigured ? links.HostCompliance() : null;
        var (template, email) = alert.Type switch
        {
            Casazen.Core.Entities.ComplianceAlertType.ConnectionInactive => (
                EmailTemplates.Names.ComplianceConnectionAlert,
                EmailTemplates.ComplianceConnectionAlert(culture, alert, property.Name, complianceUrl)),

            Casazen.Core.Entities.ComplianceAlertType.TicinoNotification => (
                EmailTemplates.Names.TicinoNotificationAlert,
                EmailTemplates.TicinoNotificationAlert(culture, alert, property.Name, complianceUrl)),

            Casazen.Core.Entities.ComplianceAlertType.SwissNotification => (
                EmailTemplates.Names.SwissNotificationAlert,
                EmailTemplates.SwissNotificationAlert(culture, alert, property.Name, complianceUrl)),
            _ => (
                EmailTemplates.Names.CompliancePeriodAlert,
                EmailTemplates.CompliancePeriodAlert(culture, alert, property.Name, complianceUrl)),
        };

        // Delivered by EmailDeliveryJob (retries on transient provider errors); a missing address or provider is logged by the queue.
        foreach (var address in emails.Where(a => !string.IsNullOrWhiteSpace(a)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!emailQueue.Enqueue(address, email, template))
            {
                logger.LogWarning(
                    "Compliance alert {Type} email of property {PropertyId} not queued (org {OrgId})",
                    alert.Type,
                    alert.PropertyId,
                    property.OrgId);
            }
        }

        var push = alert.Type switch
        {
            Casazen.Core.Entities.ComplianceAlertType.TicinoNotification => EmailTemplates.TicinoNotificationPush(culture, alert, property.Name),
            Casazen.Core.Entities.ComplianceAlertType.SwissNotification => EmailTemplates.SwissNotificationPush(culture, alert, property.Name),
            _ => EmailTemplates.CompliancePush(culture, alert, property.Name),
        };
        // The hosts of the property, and for an alert only a person with a permission can act on (the notification of the guests to the police: alloggiati.submit) the ones of them who hold it:
        // the push, the entry of the bell and the live update of the open pages go to the same people (the jobs that resolve the audience apply the permission, ComplianceAlertAudience).
        var audience = ComplianceAlertAudience.RequiredPermissionOf(alert.Type) is { } required
            ? PushAudience.PropertyHosts(alert.PropertyId, required)
            : PushAudience.PropertyHosts(alert.PropertyId);
        if (!pushNotifications.Enqueue(
                deliveryKey,
                audience,
                new PushNotificationPayload(push.Title, push.Body, PushTypes.ForComplianceAlert(alert), null, PushRoutes.Properties)))
        {
            logger.LogWarning(
                "Compliance alert {Type} push of property {PropertyId} not queued (org {OrgId})",
                alert.Type,
                alert.PropertyId,
                property.OrgId);
        }
    }

    public async Task<bool> SendCinDeadlineAlertAsync(CinDeadlineAlert alert, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(alert);

        var propertyIds = alert.PropertyIds.ToArray();
        var propertyNames = await db.Properties
            .AsNoTracking()
            .Where(p => p.OrgId == alert.OrgId && propertyIds.Contains(p.Id))
            .OrderBy(p => p.Name)
            .Select(p => p.Name)
            .ToListAsync(cancellationToken);
        if (propertyNames.Count == 0)
        {
            logger.LogWarning("CIN deadline alert of org {OrgId} skipped: none of its properties was found", alert.OrgId);
            return false;
        }

        var contactEmail = await db.Orgs
            .AsNoTracking()
            .Where(o => o.Id == alert.OrgId)
            .Select(o => o.ContactEmail)
            .FirstOrDefaultAsync(cancellationToken);
        var complianceUrl = links.IsConfigured ? links.HostCinCompliance() : null;
        var email = EmailTemplates.CinDeadlineAlert(EmailTemplates.DefaultCulture, alert.Deadline, propertyNames, complianceUrl);

        // Delivered by EmailDeliveryJob (retries on transient provider errors); a missing address or provider is logged
        // by the queue.
        if (emailQueue.Enqueue(contactEmail, email, EmailTemplates.Names.CinDeadlineAlert))
            return true;

        logger.LogWarning(
            "CIN deadline alert email of org {OrgId} not queued ({PropertyCount} properties)",
            alert.OrgId,
            propertyNames.Count);
        return false;
    }

    public async Task SendOtaStayReviewAlertAsync(OtaStayReviewAlert alert, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(alert);

        var booking = await db.Bookings
            .AsNoTracking()
            .Include(b => b.Org)
            .Include(b => b.Property)
            .Include(b => b.Guest)
            .FirstOrDefaultAsync(b => b.Id == alert.BookingId, cancellationToken);
        if (booking is null)
        {
            logger.LogWarning("OTA stay review alert skipped because booking {BookingId} was not found", alert.BookingId);
            return;
        }

        var culture = EmailTemplates.DefaultCulture;
        var guestName = $"{booking.Guest.FirstName} {booking.Guest.LastName}".Trim();
        var propertyName = booking.Property.Name;
        var channelName = EmailTemplates.OtaChannelName(booking.Source, booking.ChannelLabel);
        var bookingUrl = links.IsConfigured ? links.HostBooking(booking.Id) : null;
        var email = EmailTemplates.HostOtaStayReview(
            culture,
            alert.Reason,
            guestName,
            propertyName,
            channelName,
            booking.CheckInDate,
            booking.CheckOutDate,
            alert.ChannelCheckIn,
            alert.ChannelCheckOut,
            bookingUrl);

        if (!emailQueue.Enqueue(booking.Org?.ContactEmail, email, EmailTemplates.Names.HostOtaStayReview))
        {
            logger.LogWarning(
                "OTA stay review email of booking {BookingId} not queued (org {OrgId})", booking.Id, booking.OrgId);
        }

        var push = EmailTemplates.OtaStayReviewPush(culture, alert.Reason, propertyName, channelName, booking.CheckInDate);
        var deliveryKey = PushDeliveryKeys.OtaStayReview(booking.Id, alert.Reason, alert.ChannelCheckIn, alert.ChannelCheckOut);
        if (!pushNotifications.Enqueue(
                deliveryKey,
                PushAudience.BookingHosts(booking.Id),
                new PushNotificationPayload(push.Title, push.Body, PushTypes.OtaStayReview, booking.Id, PushRoutes.Booking(booking.Id))))
        {
            logger.LogWarning(
                "OTA stay review push of booking {BookingId} not queued (org {OrgId})", booking.Id, booking.OrgId);
        }
    }

    public async Task<bool> SendPropertyModeChangeAsync(PropertyModeNotice notice, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(notice);

        var change = await db.PropertyModeChanges
            .AsNoTracking()
            .Where(c => c.Id == notice.ChangeId)
            .Select(c => new
            {
                c.OrgId,
                c.PropertyId,
                c.FromMode,
                c.ToMode,
                c.EffectiveDate,
                c.FailureReason,
                PropertyName = c.Property.Name,
            })
            .FirstOrDefaultAsync(cancellationToken);
        if (change is null)
        {
            logger.LogWarning("Mode change notice {Kind} skipped because change {ChangeId} was not found", notice.Kind, notice.ChangeId);
            return false;
        }

        var contactEmail = await db.Orgs
            .AsNoTracking()
            .Where(o => o.Id == change.OrgId)
            .Select(o => o.ContactEmail)
            .FirstOrDefaultAsync(cancellationToken);

        var culture = EmailTemplates.DefaultCulture;
        var (template, email) = notice.Kind switch
        {
            PropertyModeNoticeKind.Scheduled => (
                EmailTemplates.Names.PropertyModeChangeScheduled,
                EmailTemplates.PropertyModeChangeScheduled(
                    culture,
                    change.PropertyName,
                    change.ToMode,
                    change.EffectiveDate,
                    links.IsConfigured ? links.HostProperty(change.PropertyId, change.FromMode) : null)),
            PropertyModeNoticeKind.Applied => (
                EmailTemplates.Names.PropertyModeChangeApplied,
                EmailTemplates.PropertyModeChangeApplied(
                    culture,
                    change.PropertyName,
                    change.ToMode,
                    change.ToMode == RentalMode.Long ? PropertyModeRules.CalendarBlockEnd(change.EffectiveDate) : null,
                    !links.IsConfigured
                        ? null
                        : change.ToMode == RentalMode.Short
                            ? links.HostPropertyActivation(change.PropertyId)
                            : links.HostProperty(change.PropertyId, change.ToMode))),
            PropertyModeNoticeKind.Failed => (
                EmailTemplates.Names.PropertyModeChangeFailed,
                EmailTemplates.PropertyModeChangeFailed(
                    culture,
                    change.PropertyName,
                    change.ToMode,
                    change.EffectiveDate,
                    change.FailureReason,
                    notice.EarliestDate,
                    links.IsConfigured ? links.HostProperty(change.PropertyId, change.FromMode) : null)),
            _ => throw new ArgumentOutOfRangeException(nameof(notice), notice.Kind, "Unknown property mode notice"),
        };

        // Delivered by EmailDeliveryJob (retries on transient provider errors); a missing address or provider is logged by
        // the queue.
        if (emailQueue.Enqueue(contactEmail, email, template))
            return true;

        logger.LogWarning(
            "Mode change {Kind} email of change {ChangeId} not queued (org {OrgId})", notice.Kind, notice.ChangeId, change.OrgId);
        return false;
    }

    public async Task SendVerificationRequiredAlertAsync(VerificationAlert alert, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(alert);

        var booking = await db.Bookings
            .AsNoTracking()
            .Include(b => b.Org)
            .Include(b => b.Property)
            .Include(b => b.Guest)
            .FirstOrDefaultAsync(b => b.Id == alert.BookingId, cancellationToken);

        if (booking is null)
        {
            logger.LogWarning("Verification required alert skipped because booking {BookingId} was not found", alert.BookingId);
            return;
        }

        var culture = EmailTemplates.DefaultCulture;
        var guestName = $"{booking.Guest.FirstName} {booking.Guest.LastName}".Trim();
        var propertyName = booking.Property.Name;
        var bookingUrl = links.IsConfigured ? links.HostBooking(booking.Id) : null;

        var email = EmailTemplates.HostVerificationRequired(culture, guestName, propertyName, booking.CheckInDate, bookingUrl);

        if (!emailQueue.Enqueue(booking.Org?.ContactEmail, email, EmailTemplates.Names.VerificationRequired))
        {
            logger.LogWarning(
                "Verification required email of booking {BookingId} not queued (org {OrgId})",
                booking.Id,
                booking.OrgId);
        }

        var push = EmailTemplates.VerificationRequiredPush(culture, guestName, propertyName);

        var deliveryKey = PushDeliveryKeys.VerificationRequired(booking.Id);

        if (!pushNotifications.Enqueue(
                deliveryKey,
                PushAudience.PropertyHosts(booking.PropertyId),
                new PushNotificationPayload(push.Title, push.Body, PushTypes.VerificationRequired, booking.Id, PushRoutes.Booking(booking.Id))))
        {
            logger.LogWarning(
                "Verification required push of booking {BookingId} not queued (org {OrgId})",
                booking.Id,
                booking.OrgId);
        }
    }
}
