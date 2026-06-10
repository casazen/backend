using Casazen.Core.Entities;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Casazen.Web.BackgroundJobs;

public class AlloggiatiDeadlineAlertJob(
    AppDbContext context,
    IAlloggiatiWebService alloggiatiWebService,
    INotificationService notificationService,
    ILogger<AlloggiatiDeadlineAlertJob> logger)
{
    public async Task ExecuteAsync()
    {
        var now = DateTime.UtcNow;
        var windowStart = now.AddHours(-24);

        var candidates = await context.Bookings
            .AsNoTracking()
            .Include(b => b.Guest)
            .Where(b => b.Status == BookingStatus.Confirmed || b.Status == BookingStatus.CheckedIn)
            .Where(b => b.CheckInDate <= now.AddHours(24) && b.CheckInDate >= windowStart.AddDays(-7))
            .ToListAsync();

        if (candidates.Count == 0)
            return;

        var bookingIds = candidates.Select(b => b.Id).ToList();
        var reports = await context.AlloggiatiWebReports
            .AsNoTracking()
            .Where(r => bookingIds.Contains(r.BookingId))
            .ToDictionaryAsync(r => r.BookingId);

        foreach (var booking in candidates)
        {
            var dataComplete = await alloggiatiWebService.ValidateGuestDataAsync(booking.GuestId);
            reports.TryGetValue(booking.Id, out var report);
            var reportStatus = report?.Status;

            var withinAlertWindow = booking.CheckInDate <= now.AddHours(24);
            var needsAlert = alloggiatiWebService.IsOverdue(booking.CheckInDate, dataComplete, reportStatus)
                || (withinAlertWindow && (!dataComplete || reportStatus == AlloggiatiWebStatus.Failed));

            if (!needsAlert)
                continue;

            logger.LogWarning(
                "Alloggiati deadline alert for booking {BookingId}: dataComplete={DataComplete}, status={Status}",
                booking.Id, dataComplete, reportStatus);

            await notificationService.SendAlloggiatiDeadlineAlertAsync(booking.Id);
        }
    }
}
