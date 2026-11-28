using System.Linq.Expressions;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;

namespace Casazen.Core.Services;

/// <summary>
/// The nights a property has taken (BK-05, A3-09, A2-13). Single definition used both by the public availability of the
/// booking site (<see cref="IPublicAvailabilityService"/>) and by the checks that refuse a booking on taken dates (the
/// booking overlap check of the repository and the calendar block check, run by the public checkout and by the host
/// bookings): a night shown as taken is a night a booking is refused on, and the other way round.
/// <para>
/// A night is a calendar date. A booking takes the nights from its check-in date (included) to its check-out date
/// (excluded); a calendar block (<see cref="CalendarBlockSource.ICalImport"/>, <see cref="CalendarBlockSource.Manual"/> or
/// <see cref="CalendarBlockSource.Octorate"/>, the nights the Octorate calendar holds closed)
/// the nights from the date of its start (included) to the date of its end (excluded). The check-out day stays free for
/// a new check-in (same-day turnover). Which bookings count (not cancelled, not an expired checkout hold, a pending
/// "pay at the property" request does) is <see cref="CheckoutHolds.OccupiesDates"/>.
/// </para>
/// </summary>
public static class PropertyOccupancy
{
    public static Expression<Func<Booking, bool>> BookingTakesNightIn(Guid propertyId, DateTime fromDate, DateTime toDate)
    {
        var from = fromDate.Date;
        var to = toDate.Date;
        return b => b.PropertyId == propertyId && b.CheckInDate.Date < to && b.CheckOutDate.Date > from;
    }

    public static Expression<Func<Booking, bool>> BookingTakesNightIn(DateTime fromDate, DateTime toDate)
    {
        var from = fromDate.Date;
        var to = toDate.Date;
        return b => b.CheckInDate.Date < to && b.CheckOutDate.Date > from;
    }

    /// <summary>
    /// A calendar block of <paramref name="propertyId"/> (iCal import or manual) that takes at least one night from
    /// <paramref name="fromDate"/> (included) to <paramref name="toDate"/> (excluded), unless the host turned it into an
    /// OTA stay that stands for it (<see cref="IsRepresentedByStay"/>, CO-21): then its nights are the stay's and count
    /// once, through the stay.
    /// </summary>
    public static Expression<Func<CalendarBlock, bool>> BlockTakesNightIn(Guid propertyId, DateTime fromDate, DateTime toDate)
    {
        var from = fromDate.Date;
        var to = toDate.Date;
        return b => b.PropertyId == propertyId && b.StartUtc.Date < to && b.EndUtc.Date > from
            && (b.BookingId == null
                || b.Booking!.Status == BookingStatus.Cancelled
                || b.Booking.CheckInDate.Date != b.StartUtc.Date
                || b.Booking.CheckOutDate.Date != b.EndUtc.Date);
    }

    public static Expression<Func<CalendarBlock, bool>> BlockTakesNightIn(DateTime fromDate, DateTime toDate)
    {
        var from = fromDate.Date;
        var to = toDate.Date;
        return b => b.StartUtc.Date < to && b.EndUtc.Date > from
            && (b.BookingId == null
                || b.Booking!.Status == BookingStatus.Cancelled
                || b.Booking.CheckInDate.Date != b.StartUtc.Date
                || b.Booking.CheckOutDate.Date != b.EndUtc.Date);
    }

    public static bool IsRepresentedByStay(CalendarBlock block, Booking? stay) =>
        stay is not null
        && block.BookingId == stay.Id
        && stay.Status != BookingStatus.Cancelled
        && stay.CheckInDate.Date == block.StartUtc.Date
        && stay.CheckOutDate.Date == block.EndUtc.Date;

    public static IEnumerable<DateTime> NightsIn(DateTime start, DateTime end, DateTime fromDate, DateTime toDate)
    {
        var night = start.Date > fromDate.Date ? start.Date : fromDate.Date;
        var last = end.Date < toDate.Date ? end.Date : toDate.Date;
        for (; night < last; night = night.AddDays(1))
            yield return night;
    }
}
