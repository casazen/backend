using Casazen.Core.Entities;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Casazen.Web.BackgroundJobs;

/// <summary>
/// Reminds the host to complete the checkout wizard when a stay ends (US-019 / #295 AC9).
/// </summary>
public class CheckoutReminderJob(
    AppDbContext context,
    INotificationService notificationService,
    ILogger<CheckoutReminderJob> logger)
{
    public async Task SendReminderAsync(Guid bookingId)
    {
        var booking = await context.Bookings
            .AsNoTracking()
            .Include(b => b.Property)
            .Include(b => b.Guest)
            .FirstOrDefaultAsync(b => b.Id == bookingId);

        if (booking is null)
        {
            logger.LogWarning("Checkout reminder skipped — booking {BookingId} not found", bookingId);
            return;
        }

        if (booking.Status != BookingStatus.CheckedIn)
        {
            logger.LogDebug(
                "Checkout reminder skipped — booking {BookingId} status is {Status}",
                bookingId,
                booking.Status);
            return;
        }

        logger.LogInformation("Sending checkout reminder for booking {BookingId}", bookingId);
        await notificationService.SendCheckoutReminderAsync(bookingId);
    }
}
