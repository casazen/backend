using Casazen.Core.Entities;
using Casazen.Core.Services;
using Casazen.Core.Utilities;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Casazen.Infrastructure.Services;

/// <summary>
/// Where the online check-in of a booking stands for its guest, in ONE place (E23/F02/US01/T01): the guest page (<see cref="GuestBookingLookupService"/>, «Le mie prenotazioni») and the
/// e-mail that confirms the booking (<see cref="BookingNotifier"/>) read it here, so the two cannot disagree on when the check-in opens. The daily job <c>GuestCheckInSendJob</c> selects
/// the same window as a filter on calendar days: the arrival from today to today + <c>CheckIn:SendWindowDays</c>, on the Europe/Rome calendar (<see cref="RomeCalendar"/>).
/// </summary>
internal static class GuestCheckInAccessRules
{
    /// <summary>
    /// The online check-in of the booking, with the rules of the daily job that e-mails the links (<c>GuestCheckInSendJob</c>): confirmed or checked-in stays not over, from
    /// <c>CheckIn:SendWindowDays</c> days before the arrival, none once the data were sent or the Alloggiati Web communication is done.
    /// </summary>
    /// <remarks>
    /// Both reads are by the id of a booking the caller already proved to be the guest's (or the one just confirmed), so they do not depend on the tenant of the request: an anonymous
    /// request has none, a guest session has the org of the booking, the signed-in guest of the mobile app has its own host org or none, and a payment webhook has none.
    /// </remarks>
    /// <param name="today">The day it is now on the Europe/Rome calendar (<c>TimeProvider.TodayInRomeAsDateOnly</c>).</param>
    /// <param name="nowUtc">The instant it is now.</param>
    internal static async Task<GuestCheckInAccess> ReadAsync(
        AppDbContext db,
        Guid bookingId,
        BookingStatus status,
        DateTime checkInDate,
        DateTime checkOutDate,
        DateOnly today,
        DateTime nowUtc,
        int sendWindowDays,
        CancellationToken cancellationToken)
    {
        var arrival = RomeCalendar.DateInRome(checkInDate);
        var departure = RomeCalendar.DateInRome(checkOutDate);
        if (status is not (BookingStatus.Confirmed or BookingStatus.CheckedIn) || departure < today)
            return new GuestCheckInAccess(GuestCheckInAccessStatus.NotApplicable, null, null);

        var sessions = await db.GuestCheckInSessions
            .IgnoreQueryFilters([AppDbContext.TenantQueryFilter])
            .AsNoTracking()
            .Where(s => s.BookingId == bookingId)
            .Select(s => new { s.Status, s.SentAt, s.ExpiresAt })
            .ToListAsync(cancellationToken);
        var communicationSent = await db.AlloggiatiWebReports
            .IgnoreQueryFilters([AppDbContext.TenantQueryFilter])
            .AnyAsync(
                r => r.BookingId == bookingId &&
                     (r.Status == AlloggiatiWebStatus.Inviato || r.Status == AlloggiatiWebStatus.InviatoManualmente),
                cancellationToken);
        if (communicationSent ||
            sessions.Any(s => s.Status is GuestCheckInSessionStatus.Completo or GuestCheckInSessionStatus.AlloggiatiInviato))
        {
            return new GuestCheckInAccess(GuestCheckInAccessStatus.Completed, null, null);
        }

        var opensOn = arrival.AddDays(-sendWindowDays);
        if (status == BookingStatus.Confirmed && today < opensOn)
            return new GuestCheckInAccess(GuestCheckInAccessStatus.NotYetOpen, opensOn, null);

        var linkSentAt = sessions
            .Where(s =>
                (s.Status is GuestCheckInSessionStatus.Inviato or GuestCheckInSessionStatus.InCompilazione) &&
                s.ExpiresAt > nowUtc)
            .Max(s => s.SentAt);
        return new GuestCheckInAccess(GuestCheckInAccessStatus.Open, null, linkSentAt);
    }
}
