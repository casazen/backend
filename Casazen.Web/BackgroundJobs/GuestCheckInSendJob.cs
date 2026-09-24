using Casazen.Core.Entities;
using Casazen.Core.Options;
using Casazen.Core.Regulatory;
using Casazen.Core.Services;
using Casazen.Core.Utilities;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Email;
using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Casazen.Web.BackgroundJobs;

/// <summary>
/// Daily 08:00 UTC job that emails guests a tokenized check-in link (AC2, US-020). Targets confirmed stays starting
/// within <c>CheckIn:SendWindowDays</c> and checked-in stays not yet checked out, when the guest data still has to be
/// collected.
/// </summary>
/// <remarks>
/// CO-09: first expires the links past their validity (A5-27), so an expired link never blocks a new one; a stay is
/// skipped while it has a usable link (open and not expired, whatever happened to its email: the host sees a failed
/// email and can resend it or copy the link), a completed check-in, a communication sent or declared sent, or complete
/// guest data (entered by the host). The email goes through <see cref="IGuestCheckInLinkEmailQueue"/>, which records
/// its real outcome on the session: a failed email no longer expires the link (A5-26).
/// </remarks>
public class GuestCheckInSendJob(
    AppDbContext db,
    IGuestCheckInService checkInService,
    IGuestCheckInLinkEmailQueue linkEmails,
    IStayGuestService stayGuests,
    PublicSiteLinks publicSiteLinks,
    IOptions<GuestCheckInOptions> options,
    ILogger<GuestCheckInSendJob> logger,
    TimeProvider? timeProvider = null)
{
    private static readonly GuestCheckInSessionStatus[] OpenStatuses =
    [
        GuestCheckInSessionStatus.Inviato,
        GuestCheckInSessionStatus.InCompilazione,
    ];

    private static readonly GuestCheckInSessionStatus[] CompletedStatuses =
    [
        GuestCheckInSessionStatus.Completo,
        GuestCheckInSessionStatus.AlloggiatiInviato,
    ];

    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;

    [DisableConcurrentExecution(JobLockTimeouts.DefaultSeconds)]
    public async Task ExecuteAsync()
    {
        if (!publicSiteLinks.IsConfigured)
        {
            logger.LogError("Guest check-in links not sent: App:PublicSiteBaseUrl is missing or invalid");
            return;
        }

        // A5-27: a link past its validity is expired, so it neither blocks a new link nor shows as open to the host.
        await checkInService.ExpireStaleSessionsAsync();

        var now = _clock.GetUtcNow().UtcDateTime;
        var today = _clock.TodayInRome();
        var windowEnd = now.AddDays(options.Value.SendWindowDays);

        var bookings = await db.Bookings
            .AsNoTracking()
            .Include(b => b.Guest)
            .Where(b =>
                (b.Status == BookingStatus.Confirmed &&
                 b.CheckInDate >= today &&
                 b.CheckInDate <= windowEnd) ||
                (b.Status == BookingStatus.CheckedIn &&
                 b.CheckOutDate >= today))
            .ToListAsync();

        if (bookings.Count == 0)
            return;

        var bookingIds = bookings.Select(b => b.Id).ToList();

        // A usable link (open and not expired) or a completed check-in: nothing to send.
        var coveredBookingIds = await db.GuestCheckInSessions
            .Where(s =>
                bookingIds.Contains(s.BookingId) &&
                (CompletedStatuses.Contains(s.Status) ||
                 (OpenStatuses.Contains(s.Status) && s.ExpiresAt >= now)))
            .Select(s => s.BookingId)
            .Distinct()
            .ToListAsync();

        // Communication already sent (receipt) or declared sent by the host: no guest data to collect.
        var completedReportBookingIds = await db.AlloggiatiWebReports
            .Where(r =>
                bookingIds.Contains(r.BookingId) &&
                (r.Status == AlloggiatiWebStatus.Inviato ||
                 r.Status == AlloggiatiWebStatus.InviatoManualmente))
            .Select(r => r.BookingId)
            .Distinct()
            .ToListAsync();

        var candidates = bookings
            .Where(b => !coveredBookingIds.Contains(b.Id) && !completedReportBookingIds.Contains(b.Id))
            .ToList();
        if (candidates.Count == 0)
            return;

        // Guest data already complete (entered by the host, CO-09): the stay no longer needs the guest's link.
        var guestsByBooking = await stayGuests.GetForBookingsAsync(candidates);

        foreach (var booking in candidates)
        {
            if (AlloggiatiRecordRules.IsDataComplete(guestsByBooking[booking.Id]))
                continue;

            try
            {
                var link = await checkInService.IssueLinkAsync(booking.Id, booking.OrgId);
                var email = await linkEmails.QueueAsync(link.SessionId, link.Token);
                logger.LogInformation(
                    "Check-in link issued for booking {BookingId}, email {EmailStatus}", booking.Id, email.Status);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to issue the check-in link for booking {BookingId}", booking.Id);
            }
        }
    }
}
