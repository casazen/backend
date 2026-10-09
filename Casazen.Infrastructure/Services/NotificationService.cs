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

        // Background job: no tenant filter; the booking id comes from the job's own query.
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
        // Validated at startup outside Development/Testing (FD-13); without it the email has no button.
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

        // Queued on PushDeliveryJob (MO-04): the key of the stage makes a retried job send it once per device.
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

    public async Task<bool> SendCinDeadlineAlertAsync(CinDeadlineAlert alert, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(alert);

        // Background job: no tenant filter; the org and its property ids come from the job's own query.
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
        // Validated at startup outside Development/Testing (FD-13); without it the email has no button.
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

        // Called by the iCal sync job: no tenant filter; the booking id comes from the sync's own query.
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

        // Called by the mode change service after its commit (a request, or the hourly job without a tenant context): the
        // change id is its own. The property's soft delete filter applies: nobody is told about a property that is gone.
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

        // Validated at startup outside Development/Testing (FD-13); without it the email has no button.
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
}
