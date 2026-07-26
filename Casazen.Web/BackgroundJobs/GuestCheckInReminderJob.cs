using Casazen.Core.Entities;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.External;
using Casazen.Core.Services;
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
    ILogger<GuestCheckInReminderJob> logger)
{
    public async Task ExecuteAsync()
    {
        var now = DateTime.UtcNow;
        var alertWindow = now.AddHours(24);

        var bookings = await db.Bookings
            .AsNoTracking()
            .Include(b => b.Guest)
            .Include(b => b.Property)
            .Include(b => b.Org)
            .Where(b =>
                b.Status == BookingStatus.Confirmed &&
                b.CheckInDate >= now &&
                b.CheckInDate <= alertWindow)
            .ToListAsync();

        if (bookings.Count == 0)
            return;

        var bookingIds = bookings.Select(b => b.Id).ToList();

        var incompleteSessions = await db.GuestCheckInSessions
            .Where(s =>
                bookingIds.Contains(s.BookingId) &&
                (s.Status == GuestCheckInSessionStatus.Inviato ||
                 s.Status == GuestCheckInSessionStatus.InCompilazione))
            .Select(s => s.BookingId)
            .Distinct()
            .ToListAsync();

        foreach (var booking in bookings.Where(b => incompleteSessions.Contains(b.Id)))
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

                var subject = $"Check-in incompleto — {booking.Property.Name} ({booking.CheckInDate:dd/MM/yyyy})";
                var html = BuildReminderHtml(booking.Property.Name, booking.CheckInDate, booking.Guest.FirstName);

                await emailService.SendEmailAsync(hostEmail, subject, html);

                logger.LogInformation(
                    "Sent incomplete check-in reminder for booking {BookingId} to host {Email}",
                    booking.Id, hostEmail);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to send reminder for booking {BookingId}", booking.Id);
            }
        }
    }

    private static string BuildReminderHtml(string propertyName, DateTime checkInDate, string guestName) =>
        $"""
        <p>Attenzione: il check-in per l'ospite <strong>{guestName}</strong> presso <strong>{propertyName}</strong>
        è previsto per <strong>{checkInDate:dd/MM/yyyy}</strong> e non è ancora stato completato.</p>
        <p>Contatta l'ospite o inserisci manualmente i dati dal gestionale.</p>
        """;
}
