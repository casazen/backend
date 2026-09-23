using Casazen.Core.Entities;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Email.Templates;
using Casazen.Infrastructure.External;
using Casazen.Core.Services;
using Casazen.Core.Utilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Casazen.Web.BackgroundJobs;

/// <summary>
/// Daily 10:00 UTC job that alerts hosts when guest check-in is incomplete within 24h of arrival (AC6, US-020).
/// </summary>
public class GuestCheckInReminderJob(
    AppDbContext db,
    IEmailService emailService,
    IPushNotificationService pushNotificationService,
    ILogger<GuestCheckInReminderJob> logger,
    TimeProvider? timeProvider = null)
{
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;

    public async Task ExecuteAsync()
    {
        var now = _clock.GetUtcNow().UtcDateTime;
        var alertWindowStart = _clock.TodayInRome();
        var alertWindow = now.AddHours(24);

        var bookings = await db.Bookings
            .AsNoTracking()
            .Include(b => b.Guest)
            .Include(b => b.Property)
            .Include(b => b.Org)
            .Where(b =>
                (b.Status == BookingStatus.Confirmed || b.Status == BookingStatus.CheckedIn) &&
                b.CheckInDate >= alertWindowStart &&
                b.CheckInDate <= alertWindow)
            .ToListAsync();

        if (bookings.Count == 0)
            return;

        var bookingIds = bookings.Select(b => b.Id).ToList();

        var completedSessionBookingIds = await db.GuestCheckInSessions
            .Where(s =>
                bookingIds.Contains(s.BookingId) &&
                (s.Status == GuestCheckInSessionStatus.Completo ||
                 s.Status == GuestCheckInSessionStatus.AlloggiatiInviato))
            .Select(s => s.BookingId)
            .Distinct()
            .ToListAsync();

        foreach (var booking in bookings.Where(b => !completedSessionBookingIds.Contains(b.Id)))
        {
            try
            {
                await pushNotificationService.SendGuestCheckInIncompleteAsync(booking.Id);

                var hostEmail = booking.Org?.ContactEmail;
                if (string.IsNullOrEmpty(hostEmail))
                {
                    logger.LogInformation(
                        "Sent incomplete check-in push reminder for booking {BookingId}; no host email configured",
                        booking.Id);
                    continue;
                }

                var email = EmailTemplates.GuestCheckInIncomplete(
                    EmailTemplates.DefaultCulture,
                    booking.Guest.FirstName,
                    booking.Property.Name,
                    booking.CheckInDate);

                // Already inside a Hangfire job: sent directly.
                var result = await emailService.SendEmailAsync(hostEmail, email.Subject, email.HtmlBody);
                if (result.Success)
                {
                    logger.LogInformation("Sent incomplete check-in reminder for booking {BookingId} to the host", booking.Id);
                }
                else
                {
                    logger.LogWarning(
                        "Incomplete check-in reminder for booking {BookingId} not sent: {ErrorDetail}",
                        booking.Id,
                        result.ErrorDetail);
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to send reminder for booking {BookingId}", booking.Id);
            }
        }
    }
}
