using System.Linq.Expressions;
using Casazen.Core.Entities;
using Casazen.Core.Utilities;

namespace Casazen.Core.Services;

/// <summary>
/// Which bookings the host KPIs count, and on which day (PC-16, A2-29, A2-36). One definition for the host dashboard
/// (<see cref="IHostDashboardService"/>) and the bookings summary of the property detail, translatable by EF and usable
/// in memory (<see cref="Expression{TDelegate}.Compile()"/>).
/// <para>
/// Days are calendar dates in Europe/Rome (<see cref="RomeCalendar"/>): a stay date is compared with the bounds of the
/// Rome day (<see cref="RomeCalendar.StartOfDayUtc"/>), so a date-only value (midnight UTC, the storage convention) and
/// an instant (e.g. <c>23:30Z</c>, already the next day in Rome) both fall on their Rome date
/// (<see cref="RomeCalendar.DateInRome"/>), never on the UTC one.
/// </para>
/// </summary>
public static class StayKpiRules
{
    /// <summary>
    /// A confirmed stay: <see cref="BookingStatus.Confirmed"/>, <see cref="BookingStatus.CheckedIn"/> or
    /// <see cref="BookingStatus.CheckedOut"/>. A cancelled booking, a checkout hold or a "pay at the property" request
    /// still waiting for the host (<see cref="BookingStatus.Pending"/>) is not one: no revenue, no arrival, no departure.
    /// </summary>
    public static Expression<Func<Booking, bool>> IsConfirmedStay() =>
        b => b.Status == BookingStatus.Confirmed || b.Status == BookingStatus.CheckedIn || b.Status == BookingStatus.CheckedOut;

    /// <summary>A confirmed stay (<see cref="IsConfirmedStay"/>) whose check-in date is <paramref name="romeDay"/>.</summary>
    public static Expression<Func<Booking, bool>> ArrivesOn(DateTime romeDay)
    {
        var (start, end) = DayBounds(romeDay);
        return b => (b.Status == BookingStatus.Confirmed || b.Status == BookingStatus.CheckedIn || b.Status == BookingStatus.CheckedOut)
            && b.CheckInDate >= start && b.CheckInDate < end;
    }

    /// <summary>A confirmed stay (<see cref="IsConfirmedStay"/>) whose check-out date is <paramref name="romeDay"/>.</summary>
    public static Expression<Func<Booking, bool>> DepartsOn(DateTime romeDay)
    {
        var (start, end) = DayBounds(romeDay);
        return b => (b.Status == BookingStatus.Confirmed || b.Status == BookingStatus.CheckedIn || b.Status == BookingStatus.CheckedOut)
            && b.CheckOutDate >= start && b.CheckOutDate < end;
    }

    /// <summary>
    /// A check-in still to come on <paramref name="todayInRome"/> or later: a <see cref="BookingStatus.Confirmed"/>
    /// booking (the arrival is not registered yet) whose check-in date is today or later. Today's arrivals are
    /// included until the host registers them; cancelled bookings and pending requests never are.
    /// </summary>
    public static Expression<Func<Booking, bool>> UpcomingCheckIn(DateTime todayInRome)
    {
        var (start, _) = DayBounds(todayInRome);
        return b => b.Status == BookingStatus.Confirmed && b.CheckInDate >= start;
    }

    /// <summary>
    /// A stay in progress on <paramref name="todayInRome"/>, up to its departure day included: a
    /// <see cref="BookingStatus.CheckedIn"/> stay from its check-in date, or a <see cref="BookingStatus.Confirmed"/> one
    /// whose check-in date has passed without the arrival being registered (CO-08). A confirmed arrival of today is
    /// <see cref="UpcomingCheckIn"/> until it is registered, then in progress: never neither.
    /// </summary>
    public static Expression<Func<Booking, bool>> InProgress(DateTime todayInRome)
    {
        var (start, end) = DayBounds(todayInRome);
        return b => b.CheckOutDate >= start
            && ((b.Status == BookingStatus.CheckedIn && b.CheckInDate < end)
                || (b.Status == BookingStatus.Confirmed && b.CheckInDate < start));
    }

    /// <summary>
    /// A departure still to come on <paramref name="todayInRome"/> or later: a confirmed or checked-in stay whose
    /// check-out date is today or later.
    /// </summary>
    public static Expression<Func<Booking, bool>> UpcomingCheckOut(DateTime todayInRome)
    {
        var (start, _) = DayBounds(todayInRome);
        return b => (b.Status == BookingStatus.Confirmed || b.Status == BookingStatus.CheckedIn) && b.CheckOutDate >= start;
    }

    /// <summary>
    /// Revenue of a confirmed stay in a period, pro rata per night: <paramref name="basePrice"/> (lodging plus cleaning,
    /// tourist tax excluded, see <see cref="Booking.BasePrice"/>) times the nights of the stay that fall from
    /// <paramref name="fromDate"/> (included) to <paramref name="toDate"/> (excluded), over the nights of the stay
    /// (<see cref="PropertyOccupancy"/>). A stay across two months counts in each for its nights there; a stay without
    /// nights counts nothing. Not rounded: round the total.
    /// </summary>
    public static decimal RevenueInPeriod(decimal basePrice, DateTime checkIn, DateTime checkOut, DateTime fromDate, DateTime toDate)
    {
        var stayNights = (checkOut.Date - checkIn.Date).Days;
        if (stayNights <= 0 || basePrice == 0)
            return 0m;

        var nightsInPeriod = PropertyOccupancy.NightsIn(checkIn, checkOut, fromDate, toDate).Count();
        return nightsInPeriod == stayNights ? basePrice : basePrice * nightsInPeriod / stayNights;
    }

    /// <summary>The Rome calendar date of a stored stay date, as midnight UTC (date-only convention).</summary>
    public static DateTime RomeDateOf(DateTime value) =>
        RomeCalendar.DateInRome(value).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);

    // The UTC instants between which a stored value falls on this Rome calendar date.
    private static (DateTime Start, DateTime End) DayBounds(DateTime romeDay)
    {
        var day = romeDay.Date;
        return (RomeCalendar.StartOfDayUtc(day), RomeCalendar.StartOfDayUtc(day.AddDays(1)));
    }
}
