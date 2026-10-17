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
/// <item><b>Properties</b>: the active short-rent properties of the scope (as <c>GET /api/properties</c> lists them, minus
/// the ones in long-term mode: <see cref="PropertyRentalModeRules.IsShortRent"/>, SR-03).</item>
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
/// <item><b>Collected</b> (SR-03, cash basis): the payments settled in the period (<see cref="PaymentCashRules"/>), by the
/// day the money came in, next to the revenue of the stays, which is by accrual. Only for a caller who may read payments.</item>
/// <item><b>Direct share</b> (SR-03): of the confirmed stays of the revenue, those that came from the booking site
/// (<see cref="BookingSource.Direct"/>).</item>
/// <item><b>Previous period</b> (SR-03, on request): the figures of the period with the same number of nights that ends
/// where this one starts (<see cref="HostDashboardPeriod.Previous"/>).</item>
/// <item><b>Overview</b> (SR-03b): the same figures of the period and of the one before, the difference between them worked out
/// here and not by the client (<see cref="HostOverviewRules"/>), the stays that arrive in the period (a request still waiting for
/// the host counts here, as it does not in <see cref="StayKpiRules"/>) and the revenue by week or month
/// (<see cref="HostOverviewSeries"/>), which adds up to the revenue of the period.</item>
/// <item><b>Reports</b> (SR-10): the revenue, the occupancy and the stays of a period of up to <see cref="HostReportRules.MaxMonths"/>
/// months cut by property or by channel, read through the code of the KPIs (the same stays, the same nights, the same rule), so
/// the total of a report is the number of the Home for the same days.</item>
/// </list>
/// </remarks>
public interface IHostDashboardService
{
    /// <summary>
    /// KPIs of <paramref name="scope"/> for the period: the month starting at <paramref name="month"/> (the current
    /// Europe/Rome month when null) or the last 30 days up to today included. Same as
    /// <see cref="GetKpisAsync(HostScope, HostDashboardQuery, CancellationToken)"/> with no property, no comparison and no
    /// cash figures.
    /// </summary>
    Task<HostDashboardKpis> GetKpisAsync(
        HostScope scope,
        HostDashboardPeriodKind kind,
        DateOnly? month,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// KPIs of <paramref name="scope"/> for the period and filters of <paramref name="query"/> (SR-03).
    /// </summary>
    /// <exception cref="Exceptions.NotFoundException">
    /// <see cref="HostDashboardQuery.PropertyId"/> is not a property of the scope (missing, another org's, or one the caller
    /// does not reach): the same 404 for all.
    /// </exception>
    Task<HostDashboardKpis> GetKpisAsync(
        HostScope scope,
        HostDashboardQuery query,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// The stays of the day, from the same rules as the KPIs (<see cref="StayKpiRules"/>): today's arrivals and departures
    /// and the check-ins to come after today (SR-03, <c>GET /api/dashboard/today</c>).
    /// </summary>
    Task<HostDashboardTodayStays> GetTodayStaysAsync(HostScope scope, CancellationToken cancellationToken = default);

    /// <summary>
    /// The numbers of the Home (SR-03b, <c>GET /api/dashboard/overview</c>): the figures of <see cref="GetKpisAsync(HostScope, HostDashboardQuery, CancellationToken)"/>
    /// (the same code, never a second copy of a rule) compared with the period before, the stays that arrive in the period, the
    /// next arrivals and, for a caller who may read money, the revenue by week or month.
    /// </summary>
    /// <exception cref="Exceptions.NotFoundException">
    /// <see cref="HostOverviewQuery.PropertyId"/> is not a property of the scope: the same 404 as <c>kpis</c>.
    /// </exception>
    /// <exception cref="ArgumentException">A <see cref="HostDashboardPeriodKind.Custom"/> query whose days are missing or not accepted by <see cref="HostDashboardPeriod.TryCustom"/>.</exception>
    Task<HostOverview> GetOverviewAsync(HostScope scope, HostOverviewQuery query, CancellationToken cancellationToken = default);

    /// <summary>
    /// The revenue of the Home cut by property (SR-10, <c>GET /api/reports/revenue-by-property</c>): the same confirmed stays and the
    /// same rule (<see cref="StayKpiRules"/>), so the total is the revenue the Home gives for the same days and each row the one the
    /// Home gives for that property. The days are checked by <see cref="HostReportRules.CheckPeriod"/> before this is called.
    /// </summary>
    /// <exception cref="Exceptions.NotFoundException"><see cref="HostReportQuery.PropertyId"/> is not a property of the scope: the same 404 as <c>kpis</c>.</exception>
    Task<HostRevenueByProperty> GetRevenueByPropertyAsync(HostScope scope, HostReportQuery query, CancellationToken cancellationToken = default);

    /// <summary>
    /// The occupancy of the Home cut by property (SR-10, <c>GET /api/reports/occupancy-by-property</c>): the same nights, per active
    /// short-rent property, and the total the Home gives.
    /// </summary>
    /// <exception cref="Exceptions.NotFoundException">As <see cref="GetRevenueByPropertyAsync"/>.</exception>
    Task<HostOccupancyByProperty> GetOccupancyByPropertyAsync(HostScope scope, HostReportQuery query, CancellationToken cancellationToken = default);

    /// <summary>
    /// The confirmed stays of the Home cut by channel, <see cref="Booking.Source"/> (SR-10, <c>GET /api/reports/bookings-by-channel</c>):
    /// the stays, nights and revenue of each, over the same stays as the revenue.
    /// </summary>
    /// <exception cref="Exceptions.NotFoundException">As <see cref="GetRevenueByPropertyAsync"/>.</exception>
    Task<HostBookingsByChannel> GetBookingsByChannelAsync(HostScope scope, HostReportQuery query, CancellationToken cancellationToken = default);

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

    /// <summary>The next 30 days, today included (SR-03): the forecast of the Home.</summary>
    Next30Days,

    /// <summary>
    /// A free period of at most <see cref="HostDashboardPeriod.MaxCustomDays"/> days, from a first to a last day chosen by the
    /// caller (SR-03b): only <c>GET /api/dashboard/overview</c> takes it.
    /// </summary>
    Custom,
}

/// <summary>
/// What a KPI request asks for (SR-03). Everything but <see cref="Kind"/> is optional.
/// </summary>
/// <param name="Kind">The kind of period.</param>
/// <param name="Month">The month of <see cref="HostDashboardPeriodKind.Month"/>; the current Europe/Rome month when null.</param>
/// <param name="PropertyId">Only this property (it must be one the scope reaches); null = every property of the scope.</param>
/// <param name="Compare">Also compute the previous period (<see cref="HostDashboardKpis.Previous"/>).</param>
/// <param name="IncludeCollected">
/// Also compute the cash figure (<see cref="HostDashboardKpis.Collected"/>). Money collected is payment data: the web layer
/// sets it only for a caller who may read payments, so the default is to leave it out.
/// </param>
/// <param name="From">First day of a <see cref="HostDashboardPeriodKind.Custom"/> period (included); ignored by any other kind.</param>
/// <param name="To">Last day of a <see cref="HostDashboardPeriodKind.Custom"/> period (included); ignored by any other kind.</param>
public sealed record HostDashboardQuery(
    HostDashboardPeriodKind Kind = HostDashboardPeriodKind.Month,
    DateOnly? Month = null,
    Guid? PropertyId = null,
    bool Compare = false,
    bool IncludeCollected = false,
    DateOnly? From = null,
    DateOnly? To = null);

/// <summary>
/// Period of the KPIs: the nights from <see cref="From"/> (included) to <see cref="To"/> (excluded), calendar dates as
/// midnight UTC. A night belongs to the date it starts on.
/// </summary>
public sealed record HostDashboardPeriod(HostDashboardPeriodKind Kind, DateTime From, DateTime To)
{
    /// <summary>Days of <see cref="HostDashboardPeriodKind.Last30Days"/>.</summary>
    public const int Last30DaysLength = 30;

    /// <summary>Days of <see cref="HostDashboardPeriodKind.Next30Days"/>.</summary>
    public const int Next30DaysLength = 30;

    /// <summary>Nights (days) of the period.</summary>
    public int Nights => (To - From).Days;

    /// <summary>
    /// The period with the same number of nights that ends where this one starts (SR-03): a month is compared with the
    /// days just before it, not with the previous calendar month, which may have another number of days. The kind is kept,
    /// the dates say which days it is.
    /// </summary>
    public HostDashboardPeriod Previous() => this with { From = From.AddDays(-Nights), To = From };

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

    /// <summary>The 30 days starting with <paramref name="todayInRome"/> (tonight included).</summary>
    public static HostDashboardPeriod ForNext30Days(DateTime todayInRome)
    {
        var from = todayInRome.Date;
        return new HostDashboardPeriod(HostDashboardPeriodKind.Next30Days, from, from.AddDays(Next30DaysLength));
    }

    /// <summary>The longest <see cref="HostDashboardPeriodKind.Custom"/> period, in days (SR-03b): a year, leap year included.</summary>
    public const int MaxCustomDays = 366;

    /// <summary>The first year a free period may touch, as for a month of <c>kpis</c>.</summary>
    public const int MinCustomYear = 2000;

    /// <summary>The last year a free period may touch, as for a month of <c>kpis</c>.</summary>
    public const int MaxCustomYear = 2100;

    /// <summary>
    /// The free period from <paramref name="firstDay"/> to <paramref name="lastDay"/>, both included (SR-03b), or false when the
    /// days are not in order, the period is longer than <see cref="MaxCustomDays"/> days or a day is out of the supported years.
    /// </summary>
    public static bool TryCustom(DateOnly firstDay, DateOnly lastDay, out HostDashboardPeriod period)
    {
        period = null!;
        if (lastDay < firstDay
            || firstDay.Year < MinCustomYear
            || lastDay.Year > MaxCustomYear
            || lastDay.DayNumber - firstDay.DayNumber + 1 > MaxCustomDays)
            return false;

        var from = firstDay.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
        period = new HostDashboardPeriod(HostDashboardPeriodKind.Custom, from, from.AddDays(lastDay.DayNumber - firstDay.DayNumber + 1));
        return true;
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
    DateTime CreatedAt)
{
    /// <summary>Booking code in its stored form (<see cref="BookingCodes.Format"/> shows it as <c>XXXXX-XXXXX</c>), SR-03.</summary>
    public string BookingCode { get; init; } = string.Empty;

    /// <summary>Where the booking came from (SR-03).</summary>
    public BookingSource Source { get; init; }

    public int NumberOfGuests { get; init; }

    /// <summary>When the host registered the arrival (UTC); null before (SR-03).</summary>
    public DateTime? ArrivedAt { get; init; }
}

/// <summary>A count and its first items.</summary>
public sealed record HostDashboardStayList(int Count, IReadOnlyList<HostDashboardStay> Items);

/// <summary>The stays of the day (SR-03): see <see cref="IHostDashboardService.GetTodayStaysAsync"/>.</summary>
/// <param name="Arrivals">Confirmed stays whose check-in date is today (registered or not).</param>
/// <param name="Departures">Confirmed stays whose check-out date is today.</param>
/// <param name="Upcoming">Check-ins still to come after today (<see cref="BookingStatus.Confirmed"/>), soonest first.</param>
public sealed record HostDashboardTodayStays(
    DateTime TodayInRome,
    HostDashboardStayList Arrivals,
    HostDashboardStayList Departures,
    HostDashboardStayList Upcoming);

/// <summary>Money collected in a period, by cash (SR-03, <see cref="PaymentCashRules"/>).</summary>
/// <param name="Amount">Euros, net of what was refunded, rounded to the cent.</param>
/// <param name="PaymentCount">Payments settled in the period (refunded ones included when only part of them was).</param>
public sealed record HostDashboardCollected(decimal Amount, int PaymentCount);

/// <summary>How many of the confirmed stays of a period came from the booking site (SR-03).</summary>
/// <param name="DirectStays">Confirmed stays with <see cref="BookingSource.Direct"/>.</param>
/// <param name="Stays">Confirmed stays with at least one night in the period (the ones of the revenue).</param>
public sealed record HostDashboardDirectShare(int DirectStays, int Stays)
{
    /// <summary>Direct / all (0..1); null without any stay.</summary>
    public decimal? Rate => Stays == 0 ? null : (decimal)DirectStays / Stays;
}

/// <summary>The figures of one period (SR-03): the current one, or the one before it.</summary>
/// <param name="Collected">Null unless the query asked for it (<see cref="HostDashboardQuery.IncludeCollected"/>).</param>
public sealed record HostDashboardFigures(
    HostDashboardPeriod Period,
    HostDashboardOccupancy Occupancy,
    decimal Revenue,
    int RevenueStayCount,
    HostDashboardCollected? Collected,
    HostDashboardDirectShare DirectShare);

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
    IReadOnlyList<HostDashboardStay> RecentBookings)
{
    /// <summary>Money collected in the period; null when it was not asked for (SR-03).</summary>
    public HostDashboardCollected? Collected { get; init; }

    /// <summary>Share of the stays that came from the booking site (SR-03).</summary>
    public HostDashboardDirectShare DirectShare { get; init; } = new(0, 0);

    /// <summary>The figures of the period before this one; null unless the query asked to compare (SR-03).</summary>
    public HostDashboardFigures? Previous { get; init; }
}

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
