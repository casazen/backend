using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.External;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Services;

public class NotificationService(
    AppDbContext db,
    IEmailService emailService,
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

    public async Task SendAlloggiatiDeadlineAlertAsync(Guid bookingId)
    {
        var booking = await db.Bookings
            .AsNoTracking()
            .Include(b => b.Org)
            .Include(b => b.Property)
            .Include(b => b.Guest)
            .FirstOrDefaultAsync(b => b.Id == bookingId);

        if (booking is null)
        {
            logger.LogWarning("Alloggiati deadline alert skipped because booking {BookingId} was not found", bookingId);
            return;
        }

        var hostEmail = booking.Org?.ContactEmail;
        if (!string.IsNullOrWhiteSpace(hostEmail))
        {
            var subject = $"Alloggiati Web in scadenza - {booking.Property.Name} ({booking.CheckInDate:dd/MM/yyyy})";
            var html = BuildAlloggiatiDeadlineHtml(booking.Property.Name, booking.CheckInDate, booking.Guest.FirstName);
            var result = await emailService.SendEmailAsync(hostEmail, subject, html);

            if (!result.Success)
            {
                logger.LogWarning(
                    "Failed to send Alloggiati deadline email for booking {BookingId}: {Error}",
                    bookingId,
                    result.ErrorDetail);
            }
        }
        else
        {
            logger.LogWarning(
                "Alloggiati deadline email skipped for booking {BookingId} because org {OrgId} has no contact email",
                bookingId,
                booking.OrgId);
        }

        await pushNotificationService.SendGuestCheckInIncompleteAsync(bookingId);
    }

    public async Task SendCheckoutReminderAsync(Guid bookingId)
    {
        logger.LogInformation("Sending checkout reminder for booking {BookingId}", bookingId);
        await pushNotificationService.SendCheckoutReminderAsync(bookingId);
        await Task.Delay(100);
    }

    public async Task SendCinDeadlineAlertAsync(string ownerId, IReadOnlyList<Guid> propertyIds, int daysUntilDeadline)
    {
        logger.LogInformation(
            "Sending CIN deadline alert for owner {OwnerId}: {PropertyCount} properties, {Days} days remaining",
            ownerId, propertyIds.Count, daysUntilDeadline);
        await Task.Delay(100);
    }

    private static string BuildAlloggiatiDeadlineHtml(string propertyName, DateTime checkInDate, string guestName) =>
        $"""
        <p>Attenzione: la comunicazione Alloggiati Web per l'ospite <strong>{guestName}</strong>
        presso <strong>{propertyName}</strong> e in scadenza per il check-in del
        <strong>{checkInDate:dd/MM/yyyy}</strong>.</p>
        <p>Completa o correggi i dati dell'ospite e invia la comunicazione dal gestionale.</p>
        """;
}