using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;

namespace Casazen.Core.Services;

/// <summary>
/// KPIs of the host dashboard, computed on the server for a period (PC-16, A2-29), and the state of the iCal import
/// feeds of the host's properties. Every read is limited to the caller's <see cref="HostScope"/> (org, and owned
/// properties unless org-wide), in SQL.
/// </summary>
/// <remarks>
/// Definitions (also in <c>docs/TECHNICAL.md</c>, "Host dashboard"):
/// <list type="bullet">
/// <item><b>Properties</b>: the active properties of the scope (as <c>GET /api/properties</c>).</item>
/// <item><b>Occupancy</b> = occupied nights / available nights of the period, over those properties. A night is taken
/// as in <see cref="PropertyOccupancy"/> (the same nights the booking site shows as taken): a booking that occupies its
/// dates (<see cref="CheckoutHolds.OccupiesDates"/>: not cancelled, not an expired hold) or a block imported by an iCal
/// feed. A night closed only by a manual block (<see cref="CalendarBlockSource.Manual"/>: owner stay, maintenance) is
/// neither occupied nor available: it leaves the denominator. Available nights = nights of the period × properties −
/// those closed nights.</item>
/// <item><b>Revenue</b> of the period: confirmed stays (<see cref="StayKpiRules.IsConfirmedStay"/>) pro rata per night
/// (<see cref="StayKpiRules.RevenueInPeriod"/>) on <see cref="Booking.BasePrice"/> (lodging plus cleaning, tourist tax
/// excluded), in euros.</item>
/// <item><b>Arrivals / departures today</b>: confirmed stays whose check-in / check-out date is today in Europe/Rome
/// (<see cref="StayKpiRules.ArrivesOn"/>, <see cref="StayKpiRules.DepartsOn"/>).</item>
/// <item><b>Upcoming check-ins</b>: <see cref="StayKpiRules.UpcomingCheckIn"/> (confirmed, from today, never a
/// cancelled booking), soonest first.</item>
/// </list>
/// </remarks>
public interface IHostDashboardService
{
    /// <summary>
    /// KPIs of <paramref name="scope"/> for the period: the month starting at <paramref name="month"/> (the current
    /// Europe/Rome month when null) or the last 30 days up to today included.
    /// </summary>
    Task<HostDashboardKpis> GetKpisAsync(
        HostScope scope,
        HostDashboardPeriodKind kind,
        DateOnly? month,
        CancellationToken cancellationToken = default);

    /// <summary>The iCal import feeds of the properties of <paramref name="scope"/>, by property name then creation.</summary>
    Task<IReadOnlyList<HostDashboardIcalFeed>> GetIcalFeedsAsync(HostScope scope, CancellationToken cancellationToken = default);
}

/// <summary>Kind of period of the dashboard KPIs.</summary>
public enum HostDashboardPeriodKind
{
    /// <summary>A calendar month (the current one by default).</summary>
    Month,

    /// <summary>The last 30 days, today included.</summary>
    Last30Days,
}

/// <summary>
/// Period of the KPIs: the nights from <see cref="From"/> (included) to <see cref="To"/> (excluded), calendar dates as
/// midnight UTC. A night belongs to the date it starts on.
/// </summary>
public sealed record HostDashboardPeriod(HostDashboardPeriodKind Kind, DateTime From, DateTime To)
{
    /// <summary>Days of <see cref="HostDashboardPeriodKind.Last30Days"/>.</summary>
    public const int Last30DaysLength = 30;

    /// <summary>Nights (days) of the period.</summary>
    public int Nights => (To - From).Days;

    /// <summary>The calendar month of <paramref name="month"/> (its day is ignored).</summary>
    public static HostDashboardPeriod ForMonth(DateOnly month)
    {
        var from = new DateTime(month.Year, month.Month, 1, 0, 0, 0, DateTimeKind.Utc);
        return new HostDashboardPeriod(HostDashboardPeriodKind.Month, from, from.AddMonths(1));
    }

    /// <summary>The 30 days ending with <paramref name="todayInRome"/> (tonight included).</summary>
    public static HostDashboardPeriod ForLast30Days(DateTime todayInRome)
    {
        var to = todayInRome.Date.AddDays(1);
        return new HostDashboardPeriod(HostDashboardPeriodKind.Last30Days, to.AddDays(-Last30DaysLength), to);
    }
}

/// <summary>Occupancy of the period (see <see cref="IHostDashboardService"/>).</summary>
/// <param name="OccupiedNights">Property-nights taken by a booking or an imported iCal block.</param>
/// <param name="AvailableNights">Property-nights of the period, minus <paramref name="ClosedNights"/>.</param>
/// <param name="ClosedNights">Property-nights closed only by a manual block, out of the availability.</param>
public sealed record HostDashboardOccupancy(int OccupiedNights, int AvailableNights, int ClosedNights)
{
    /// <summary>Occupied / available (0..1); null when nothing is available (no property, everything closed).</summary>
    public decimal? Rate => AvailableNights == 0 ? null : (decimal)OccupiedNights / AvailableNights;
}

/// <summary>A stay listed by the dashboard (arrivals, departures, upcoming check-ins, recent bookings).</summary>
/// <param name="CheckInDate">Europe/Rome check-in date, midnight UTC.</param>
/// <param name="CheckOutDate">Europe/Rome check-out date, midnight UTC.</param>
public sealed record HostDashboardStay(
    Guid BookingId,
    Guid PropertyId,
    string PropertyName,
    string GuestName,
    DateTime CheckInDate,
    DateTime CheckOutDate,
    BookingStatus Status,
    decimal TotalPrice,
    DateTime CreatedAt);

/// <summary>A count and its first items.</summary>
public sealed record HostDashboardStayList(int Count, IReadOnlyList<HostDashboardStay> Items);

/// <summary>KPIs of the host dashboard for a period.</summary>
/// <param name="Revenue">Revenue of the period in euros, rounded to the cent.</param>
/// <param name="RevenueStayCount">Confirmed stays with at least one night in the period.</param>
/// <param name="RecentBookings">The last bookings created, any status (activity list).</param>
public sealed record HostDashboardKpis(
    HostDashboardPeriod Period,
    DateTime TodayInRome,
    int PropertyCount,
    HostDashboardOccupancy Occupancy,
    decimal Revenue,
    int RevenueStayCount,
    HostDashboardStayList ArrivalsToday,
    HostDashboardStayList DeparturesToday,
    HostDashboardStayList UpcomingCheckIns,
    IReadOnlyList<HostDashboardStay> RecentBookings);

/// <summary>State of one iCal import feed (PC-11) for the dashboard widget; never its URL.</summary>
/// <param name="LastError">Stored error code of the last sync (translated by the web layer).</param>
public sealed record HostDashboardIcalFeed(
    Guid FeedId,
    Guid PropertyId,
    string PropertyName,
    ICalFeedChannel Channel,
    string? Label,
    DateTime? LastImportAt,
    PropertyICalImportStatus? LastImportStatus,
    string? LastError);
