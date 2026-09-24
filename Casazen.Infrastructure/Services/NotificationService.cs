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
    ILogger<NotificationService> logger) : INotificationService
{
    public async Task SendBookingConfirmationAsync(Guid bookingId)
    {
        logger.LogInformation("Sending booking confirmation for {BookingId}", bookingId);
        await Task.Delay(100); // Simulate email send
    }

    public async Task SendPaymentReceiptAsync(Guid paymentId)
    {
        logger.LogInformation("Sending payment receipt for {PaymentId}", paymentId);
        await Task.Delay(100); // Simulate email send
    }

    public async Task SendPropertyUpdateAsync(Guid propertyId)
    {
        logger.LogInformation("Sending property update notification for {PropertyId}", propertyId);
        await Task.Delay(100); // Simulate email send
    }

    public async Task SendOtaSyncNotificationAsync(Guid propertyId, string platform)
    {
        logger.LogInformation("Sending OTA sync notification for {PropertyId} on {Platform}", propertyId, platform);
        await Task.Delay(100); // Simulate email send
    }

    public async Task SendRefundNotificationAsync(Guid paymentId)
    {
        logger.LogInformation("Sending refund notification for {PaymentId}", paymentId);
        await Task.Delay(100);
    }

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
        var (template, email) = alert.Kind switch
        {
            StayAlertKind.GuestDataMissing => (
                EmailTemplates.Names.GuestCheckInIncomplete,
                EmailTemplates.GuestCheckInIncomplete(culture, guestName, propertyName, booking.CheckInDate)),
            StayAlertKind.AlloggiatiDeadlineApproaching => (
                EmailTemplates.Names.AlloggiatiDeadline,
                EmailTemplates.AlloggiatiDeadline(culture, guestName, propertyName, booking.CheckInDate, alert.ShortStay, alert.DeadlineUtc)),
            StayAlertKind.AlloggiatiOverdue => (
                EmailTemplates.Names.AlloggiatiOverdue,
                EmailTemplates.AlloggiatiOverdue(
                    culture, guestName, propertyName, booking.CheckInDate, alert.DeadlineUtc, alert.ReminderNumber, alert.MaxReminders)),
            StayAlertKind.AlloggiatiFailed => (
                EmailTemplates.Names.AlloggiatiFailed,
                EmailTemplates.AlloggiatiFailed(culture, guestName, propertyName, booking.CheckInDate)),
            StayAlertKind.CheckoutReminder => (
                EmailTemplates.Names.CheckoutReminder,
                EmailTemplates.CheckoutReminder(culture, guestName, propertyName, booking.CheckOutDate)),
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

    public async Task SendCinDeadlineAlertAsync(string ownerId, IReadOnlyList<Guid> propertyIds, int daysUntilDeadline)
    {
        logger.LogInformation(
            "Sending CIN deadline alert for owner {OwnerId}: {PropertyCount} properties, {Days} days remaining",
            ownerId, propertyIds.Count, daysUntilDeadline);
        await Task.Delay(100);
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
