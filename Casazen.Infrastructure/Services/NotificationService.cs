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
    IPushNotificationService pushNotificationService,
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

        if (alert.Kind == StayAlertKind.CheckoutReminder)
        {
            await pushNotificationService.SendCheckoutReminderAsync(booking.Id, cancellationToken);
            return;
        }

        var push = EmailTemplates.StayAlertPush(culture, alert.Kind, propertyName, booking.CheckInDate);
        await pushNotificationService.SendToBookingHostsAsync(
            new PushNotificationPayload(push.Title, push.Body, PushType(alert.Kind), booking.Id, $"/bookings/{booking.Id}"),
            cancellationToken);
    }

    /// <summary>
    /// No email or push exists yet for the CIN deadline (CO-20): the alert is only logged as not delivered, instead of
    /// pretending to send it.
    /// </summary>
    public Task SendCinDeadlineAlertAsync(string ownerId, IReadOnlyList<Guid> propertyIds, int daysUntilDeadline)
    {
        logger.LogWarning(
            "CIN deadline alert for owner {OwnerId} not delivered: no email or push for it yet ({PropertyCount} properties, {Days} days remaining)",
            ownerId,
            propertyIds.Count,
            daysUntilDeadline);
        return Task.CompletedTask;
    }

    /// <summary>Push <c>type</c> of each Alloggiati alert (the app opens the booking from the route).</summary>
    private static string PushType(StayAlertKind kind) => kind switch
    {
        StayAlertKind.GuestDataMissing => "guest-data-missing",
        StayAlertKind.AlloggiatiDeadlineApproaching => "alloggiati-deadline",
        StayAlertKind.AlloggiatiOverdue => "alloggiati-overdue",
        StayAlertKind.AlloggiatiFailed => "alloggiati-failed",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "No push type for this alert."),
    };
}
