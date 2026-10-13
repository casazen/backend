using System.Linq.Expressions;
using Casazen.Core.Entities;

namespace Casazen.Core.Services;

/// <summary>
/// Which bookings and calendar blocks the host calendar shows for a range of days (MO-06, A6-11, A2-16): the rule of
/// <c>GET /api/bookings/calendar?startDate=2026-09-01&amp;endDate=2026-09-30</c>, read by the web console and the app.
/// <para>
/// The two ends are <b>stay dates</b> (calendar days, without time or time zone), both included. Stay and block dates
/// are stored as midnight UTC of their day (FD-06), so the range is never converted to the property time zone:
/// converting it moved the whole month back one day in Italy and hid the stays starting on its last day.
/// </para>
/// <para>
/// A stay or block is shown on every day from its arrival day to its departure day, both included: a stay from 28/08 to
/// 01/09 is in the September calendar for its departure, a stay from 30/09 to 02/10 for its arrival. The nights a
/// property has taken (availability, overlap checks) are <see cref="PropertyOccupancy"/>'s, not this.
/// </para>
/// </summary>
public static class HostCalendarRange
{
    /// <summary>A booking of <paramref name="propertyId"/> with a day from <paramref name="firstDay"/> to <paramref name="lastDay"/>.</summary>
    public static Expression<Func<Booking, bool>> BookingShownIn(Guid propertyId, DateTime firstDay, DateTime lastDay)
    {
        var from = StartOfDay(firstDay);
        var toExclusive = StartOfDay(lastDay).AddDays(1);
        return b => b.PropertyId == propertyId && b.CheckInDate < toExclusive && b.CheckOutDate >= from;
    }

    /// <summary>
    /// A calendar block of <paramref name="propertyId"/> (iCal import or manual) with a day from
    /// <paramref name="firstDay"/> to <paramref name="lastDay"/>.
    /// </summary>
    public static Expression<Func<CalendarBlock, bool>> BlockShownIn(Guid propertyId, DateTime firstDay, DateTime lastDay)
    {
        var from = StartOfDay(firstDay);
        var toExclusive = StartOfDay(lastDay).AddDays(1);
        return b => b.PropertyId == propertyId && b.StartUtc < toExclusive && b.EndUtc >= from;
    }

    /// <summary>
    /// The day of a stored stay or block date (midnight UTC of that day) as a date without time zone: it is written as
    /// <c>2026-09-30T00:00:00</c>, the same day for every client whatever its time zone.
    /// </summary>
    public static DateTime StayDate(DateTime stored) => DateTime.SpecifyKind(stored.Date, DateTimeKind.Unspecified);

    private static DateTime StartOfDay(DateTime day) => DateTime.SpecifyKind(day.Date, DateTimeKind.Utc);
}
