using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Email.Templates;
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
            // Runs inside the Hangfire alert job: the email is sent directly, not queued again.
            var email = EmailTemplates.AlloggiatiDeadline(
                EmailTemplates.DefaultCulture,
                booking.Guest.FirstName,
                booking.Property.Name,
                booking.CheckInDate);
            var result = await emailService.SendEmailAsync(hostEmail, email.Subject, email.HtmlBody);

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
}