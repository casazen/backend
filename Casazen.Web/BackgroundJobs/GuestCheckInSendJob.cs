using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Options;
using Casazen.Core.Regulatory;
using Casazen.Core.Services;
using Casazen.Core.Utilities;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Email;
using Casazen.Infrastructure.Services;
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
/// its real outcome on the session: a failed email no longer expires the link (A5-26). A second stage sends, once per stay, the
/// reminder of the data still missing just before arrival (NF-40, <c>CheckIn:ReminderDaysBefore</c>).
/// E23/F01/US01/T01, T02 (NF-122): a stay with no e-mail address (one from a portal) is handed to <see cref="ICheckInLinkDeliveryService"/>, which tries the portal chat and the
/// contact of the guest where their flags are on and always records where the link stands; the first stage then queues no e-mail for a link the service took over, and the
/// reminder leaves such a stay alone (a new link would replace the one the guest already has).
/// </remarks>
public class GuestCheckInSendJob(
    AppDbContext db,
    IGuestCheckInService checkInService,
    IGuestCheckInLinkEmailQueue linkEmails,
    IStayGuestService stayGuests,
    PublicSiteLinks publicSiteLinks,
    IOptions<GuestCheckInOptions> options,
    ILogger<GuestCheckInSendJob> logger,
    TimeProvider? timeProvider = null,
    ICheckInLinkDeliveryService? deliveries = null)
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
        // Calendar window in Europe/Rome (QA-CLOCK): arrivals from today to today + SendWindowDays, whatever the hour
        // of the run, as the guest page opens the check-in (GuestBookingLookupService). An instant bound would drop the
        // last day of the window between 22:00 and 24:00 UTC, when Rome is already on the next day.
        var windowEnd = today.AddDays(options.Value.SendWindowDays);

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

        if (bookings.Count > 0)
            await SendLinksAsync(bookings, now);

        await RemindAsync(today, now);
    }

    /// <summary>First stage: the link of the stays that have none usable.</summary>
    private async Task SendLinksAsync(List<Booking> bookings, DateTime now)
    {
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

        // E03/F04/US02: a stay whose identity verification is with an operator (rejected by one, or a hand-off still open) gets no new link: a new
        // session would start the verification again and could open the lock code against the decision of a person.
        var withOperator = await VerificationHandOff.BookingsWithOperatorAsync(db, candidates.Select(b => b.Id).ToList(), CancellationToken.None);
        candidates = candidates.Where(b => !withOperator.Contains(b.Id)).ToList();
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

                // E23/F01/US01/T01: a stay with no e-mail address is handed to the delivery service, which records where the link stands and, where the flags of the portal chat and of
                // the contact of the guest are on, takes it to the guest. A link it took over needs no e-mail from here (it queues one itself when it finds an address); any other
                // goes on as it always did, and the failed e-mail (no recipient) is what the host sees on the session.
                if (deliveries is not null && string.IsNullOrWhiteSpace(booking.Guest?.Email))
                {
                    var delivery = await deliveries.DeliverAsync(booking.Id, link);
                    if (delivery.IsAutomatic)
                    {
                        logger.LogInformation(
                            "Check-in link issued for booking {BookingId}, delivery {State} by {Channel}", booking.Id, delivery.State, delivery.Channel);
                        continue;
                    }
                }

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

    /// <summary>
    /// Second stage (NF-40, AC-NF40-3): the one reminder of a stay whose guest data are still missing just before arrival. A confirmed booking
    /// arriving within <c>CheckIn:ReminderDaysBefore</c> days (today included; 0 turns the stage off), with incomplete data, no communication
    /// sent or declared sent, no completed check-in and no session that already carried a reminder, gets a new link (the old one expires: the
    /// answers of the form are not kept on the server before the submit, so nothing is lost there; the identity verification is another matter,
    /// its consent, attempts and hand-off sit on the session, so a stay whose verification is with an operator is not reminded at all,
    /// <see cref="VerificationHandOff"/>) and the reminder email in the language of its latest session.
    /// A reminder follows a link the guest already received: a stay with no link yet, or whose latest link was issued today (Europe/Rome), by
    /// the first stage or by the host, is not reminded that day. Transactional: the booker's contact email only, no promotion. One booking's
    /// failure never stops the others.
    /// </summary>
    private async Task RemindAsync(DateTime today, DateTime now)
    {
        var days = options.Value.ReminderDaysBefore;
        if (days <= 0)
            return;

        var windowEnd = today.AddDays(days);
        var bookings = await db.Bookings
            .AsNoTracking()
            .Include(b => b.Guest)
            .Where(b => b.Status == BookingStatus.Confirmed && b.CheckInDate >= today && b.CheckInDate <= windowEnd)
            .ToListAsync();
        if (bookings.Count == 0)
            return;

        var bookingIds = bookings.Select(b => b.Id).ToList();

        // One reminder per stay, whatever the number of runs; none once the guest completed the check-in.
        var notToRemind = await db.GuestCheckInSessions
            .Where(s =>
                bookingIds.Contains(s.BookingId) &&
                (s.ReminderSentAt != null || CompletedStatuses.Contains(s.Status)))
            .Select(s => s.BookingId)
            .Distinct()
            .ToListAsync();

        // The latest link of each stay: a reminder only after a link issued before today.
        var todayInRome = DateOnly.FromDateTime(today);
        var latestLinks = (await db.GuestCheckInSessions
                .Where(s => bookingIds.Contains(s.BookingId))
                .GroupBy(s => s.BookingId)
                .Select(g => new { BookingId = g.Key, CreatedAt = g.Max(s => s.CreatedAt) })
                .ToListAsync())
            .ToDictionary(l => l.BookingId, l => l.CreatedAt);
        notToRemind.AddRange(bookingIds.Where(id =>
            !latestLinks.TryGetValue(id, out var issuedAt) || RomeCalendar.DateInRome(issuedAt) >= todayInRome));

        // The communication sent (receipt) or declared sent: no guest data to collect.
        var reported = await db.AlloggiatiWebReports
            .Where(r =>
                bookingIds.Contains(r.BookingId) &&
                (r.Status == AlloggiatiWebStatus.Inviato ||
                 r.Status == AlloggiatiWebStatus.InviatoManualmente))
            .Select(r => r.BookingId)
            .Distinct()
            .ToListAsync();

        var candidates = bookings.Where(b => !notToRemind.Contains(b.Id) && !reported.Contains(b.Id)).ToList();
        if (candidates.Count == 0)
            return;

        // E03/F04/US02: nor is a stay whose identity verification is with an operator reminded: the new link replaces the session that holds the
        // consent, the attempts and the hand-off, and the guest would start the verification again.
        var withOperator = await VerificationHandOff.BookingsWithOperatorAsync(db, candidates.Select(b => b.Id).ToList(), CancellationToken.None);
        candidates = candidates.Where(b => !withOperator.Contains(b.Id)).ToList();
        if (candidates.Count == 0)
            return;

        // E23/F01/US01/T01: a stay with no e-mail address whose link the delivery service took over is not reminded: a new link would replace the one the guest already has
        // (in the chat of the portal), or the one the host is about to copy, and the reminder could reach nobody.
        if (deliveries is not null)
        {
            var candidateIds = candidates.Select(b => b.Id).ToList();
            var heldByTheService = await db.CheckInLinkDeliveries
                .Where(d => candidateIds.Contains(d.BookingId) && d.EndedAt == null && d.Channel != CheckInLinkChannel.Manual)
                .Select(d => d.BookingId)
                .ToListAsync();
            candidates = candidates
                .Where(b => !heldByTheService.Contains(b.Id) || !string.IsNullOrWhiteSpace(b.Guest?.Email))
                .ToList();
            if (candidates.Count == 0)
                return;
        }

        var guestsByBooking = await stayGuests.GetForBookingsAsync(candidates);
        foreach (var booking in candidates)
        {
            if (AlloggiatiRecordRules.IsDataComplete(guestsByBooking[booking.Id]))
                continue;

            try
            {
                // The language of the latest session where the guest chose one (a link the host issued later has none of its own).
                var language = await db.GuestCheckInSessions
                    .Where(s => s.BookingId == booking.Id && s.Language != null)
                    .OrderByDescending(s => s.CreatedAt)
                    .Select(s => s.Language)
                    .FirstOrDefaultAsync();

                var link = await checkInService.IssueLinkAsync(booking.Id, booking.OrgId);

                // Marked before the email is queued: the email job reads the mark to write the reminder, and a second run finds it.
                var session = await db.GuestCheckInSessions.SingleAsync(s => s.Id == link.SessionId);
                session.Language = language;
                session.ReminderSentAt = now;
                session.UpdatedAt = now;
                await db.SaveChangesAsync();

                var email = await linkEmails.QueueAsync(link.SessionId, link.Token);
                logger.LogInformation(
                    "Check-in reminder issued for booking {BookingId}, email {EmailStatus}", booking.Id, email.Status);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to issue the check-in reminder for booking {BookingId}", booking.Id);
            }
        }
    }
}
