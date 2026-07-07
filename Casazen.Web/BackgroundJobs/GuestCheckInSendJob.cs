using Casazen.Core.Entities;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.External;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Casazen.Web.BackgroundJobs;

/// <summary>
/// Daily 08:00 UTC job that emails guests with a tokenized check-in link (AC2, US-020).
/// Targets Confirmed bookings whose check-in is within the configured window (default 3 days).
/// </summary>
public class GuestCheckInSendJob(
    AppDbContext db,
    IGuestCheckInService checkInService,
    IEmailService emailService,
    IConfiguration configuration,
    ILogger<GuestCheckInSendJob> logger)
{
    public async Task ExecuteAsync()
    {
        var sendWindowDays = configuration.GetValue("CheckIn:SendWindowDays", 3);
        var now = DateTime.UtcNow;
        var windowEnd = now.AddDays(sendWindowDays);

        var bookings = await db.Bookings
            .AsNoTracking()
            .Include(b => b.Guest)
            .Include(b => b.Property)
            .Include(b => b.Org)
            .Where(b =>
                b.Status == BookingStatus.Confirmed &&
                b.CheckInDate >= now.Date &&
                b.CheckInDate <= windowEnd)
            .ToListAsync();

        if (bookings.Count == 0)
            return;

        var bookingIds = bookings.Select(b => b.Id).ToList();
        var existingActiveSessionBookingIds = await db.GuestCheckInSessions
            .Where(s =>
                bookingIds.Contains(s.BookingId) &&
                s.Status != GuestCheckInSessionStatus.Scaduto)
            .Select(s => s.BookingId)
            .Distinct()
            .ToListAsync();

        var pending = bookings
            .Where(b => !existingActiveSessionBookingIds.Contains(b.Id))
            .ToList();

        var baseUrl = configuration["App:PublicSiteBaseUrl"] ?? "https://casazen-app.vercel.app";

        foreach (var booking in pending)
        {
            try
            {
                var token = await checkInService.CreateSessionAsync(booking.Id, booking.OrgId);
                var link = $"{baseUrl}/check-in/{token}";
                var subject = $"Completa il check-in per il tuo soggiorno — {booking.Property.Name}";
                var html = BuildEmailHtml(booking.Guest.FirstName, booking.Property.Name, booking.CheckInDate, link);

                await emailService.SendEmailAsync(booking.Guest.Email, subject, html);

                logger.LogInformation(
                    "Sent check-in link for booking {BookingId} to guest {GuestId}",
                    booking.Id, booking.GuestId);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to send check-in link for booking {BookingId}", booking.Id);
            }
        }
    }

    private static string BuildEmailHtml(string guestName, string propertyName, DateTime checkInDate, string link) =>
        $"""
        <p>Gentile {guestName},</p>
        <p>Il tuo soggiorno presso <strong>{propertyName}</strong> inizia il <strong>{checkInDate:dd/MM/yyyy}</strong>.</p>
        <p>Completa il check-in in anticipo cliccando il link qui sotto:</p>
        <p><a href="{link}">Completa il check-in</a></p>
        <p>Il link è valido per 7 giorni.</p>
        """;
}
