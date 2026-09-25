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
/// (excluded); a calendar block (<see cref="CalendarBlockSource.ICalImport"/> or <see cref="CalendarBlockSource.Manual"/>)
/// the nights from the date of its start (included) to the date of its end (excluded). The check-out day stays free for
/// a new check-in (same-day turnover). Which bookings count (not cancelled, not an expired checkout hold, a pending
/// "pay at the property" request does) is <see cref="CheckoutHolds.OccupiesDates"/>.
/// </para>
/// </summary>
public static class PropertyOccupancy
{
    /// <summary>
    /// A booking of <paramref name="propertyId"/> that takes at least one night from <paramref name="fromDate"/>
    /// (included) to <paramref name="toDate"/> (excluded). Combine it with <see cref="CheckoutHolds.OccupiesDates"/>.
    /// </summary>
    public static Expression<Func<Booking, bool>> BookingTakesNightIn(Guid propertyId, DateTime fromDate, DateTime toDate)
    {
        var from = fromDate.Date;
        var to = toDate.Date;
        return b => b.PropertyId == propertyId && b.CheckInDate.Date < to && b.CheckOutDate.Date > from;
    }

    /// <summary>
    /// A booking of any property that takes at least one night from <paramref name="fromDate"/> (included) to
    /// <paramref name="toDate"/> (excluded): same rule as <see cref="BookingTakesNightIn(Guid, DateTime, DateTime)"/>, for
    /// reads over many properties (host dashboard, PC-16) that filter the properties themselves.
    /// </summary>
    public static Expression<Func<Booking, bool>> BookingTakesNightIn(DateTime fromDate, DateTime toDate)
    {
        var from = fromDate.Date;
        var to = toDate.Date;
        return b => b.CheckInDate.Date < to && b.CheckOutDate.Date > from;
    }

    /// <summary>
    /// A calendar block of <paramref name="propertyId"/> (iCal import or manual) that takes at least one night from
    /// <paramref name="fromDate"/> (included) to <paramref name="toDate"/> (excluded).
    /// </summary>
    public static Expression<Func<CalendarBlock, bool>> BlockTakesNightIn(Guid propertyId, DateTime fromDate, DateTime toDate)
    {
        var from = fromDate.Date;
        var to = toDate.Date;
        return b => b.PropertyId == propertyId && b.StartUtc.Date < to && b.EndUtc.Date > from;
    }

    /// <summary>
    /// A calendar block of any property that takes at least one night from <paramref name="fromDate"/> (included) to
    /// <paramref name="toDate"/> (excluded): same rule as <see cref="BlockTakesNightIn(Guid, DateTime, DateTime)"/>.
    /// </summary>
    public static Expression<Func<CalendarBlock, bool>> BlockTakesNightIn(DateTime fromDate, DateTime toDate)
    {
        var from = fromDate.Date;
        var to = toDate.Date;
        return b => b.StartUtc.Date < to && b.EndUtc.Date > from;
    }

    /// <summary>
    /// The nights taken by a stay or block from <paramref name="start"/> to <paramref name="end"/> (see the class
    /// remarks) that fall from <paramref name="fromDate"/> (included) to <paramref name="toDate"/> (excluded), in order.
    /// </summary>
    public static IEnumerable<DateTime> NightsIn(DateTime start, DateTime end, DateTime fromDate, DateTime toDate)
    {
        var night = start.Date > fromDate.Date ? start.Date : fromDate.Date;
        var last = end.Date < toDate.Date ? end.Date : toDate.Date;
        for (; night < last; night = night.AddDays(1))
            yield return night;
    }
}
