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
/// Targets upcoming Confirmed bookings and already CheckedIn stays that still need self-service data.
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
                (b.Status == BookingStatus.Confirmed &&
                 b.CheckInDate >= now.Date &&
                 b.CheckInDate <= windowEnd) ||
                (b.Status == BookingStatus.CheckedIn &&
                 b.CheckOutDate >= now.Date))
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

        var completedReportBookingIds = await db.AlloggiatiWebReports
            .Where(r =>
                bookingIds.Contains(r.BookingId) &&
                (r.Status == AlloggiatiWebStatus.Submitted ||
                 r.Status == AlloggiatiWebStatus.Confirmed))
            .Select(r => r.BookingId)
            .Distinct()
            .ToListAsync();

        var pending = bookings
            .Where(b =>
                !existingActiveSessionBookingIds.Contains(b.Id) &&
                !completedReportBookingIds.Contains(b.Id))
            .ToList();

        var baseUrl = configuration["App:PublicSiteBaseUrl"] ?? "https://casazen-app.vercel.app";

        foreach (var booking in pending)
        {
            string? token = null;

            try
            {
                token = await checkInService.CreateSessionAsync(booking.Id, booking.OrgId);
                var link = $"{baseUrl}/checkin/{token}";
                var subject = $"Completa il check-in per il tuo soggiorno — {booking.Property.Name}";
                var html = BuildEmailHtml(booking.Guest.FirstName, booking.Property.Name, booking.CheckInDate, link);

                var result = await emailService.SendEmailAsync(booking.Guest.Email, subject, html);
                if (!result.Success)
                {
                    await ExpireUndeliveredTokenAsync(token, booking.Id);
                    logger.LogError(
                        "Failed to send check-in link for booking {BookingId}: {ErrorDetail}",
                        booking.Id,
                        result.ErrorDetail ?? "email service returned failure");
                    continue;
                }

                logger.LogInformation(
                    "Sent check-in link for booking {BookingId} to guest {GuestId}",
                    booking.Id, booking.GuestId);
            }
            catch (Exception ex)
            {
                if (token is not null)
                    await ExpireUndeliveredTokenAsync(token, booking.Id);

                logger.LogError(ex, "Failed to send check-in link for booking {BookingId}", booking.Id);
            }
        }
    }

    private async Task ExpireUndeliveredTokenAsync(string token, Guid bookingId)
    {
        try
        {
            await checkInService.ExpireTokenAsync(token);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to expire undelivered check-in token for booking {BookingId}", bookingId);
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
