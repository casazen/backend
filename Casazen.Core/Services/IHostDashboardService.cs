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
/// <item><b>ADR and RevPAR</b> (NF-96): ADR = the revenue of the confirmed stays over their nights in the period; RevPAR = the same
/// revenue over the available nights of the Home. Both are null, never zero, when their denominator is zero. The performance report
/// and the trend give them per property, per channel or for the whole scope, and next to the same calendar days one year earlier.</item>
/// </list>
/// </remarks>
public interface IHostDashboardService
{
    Task<HostDashboardKpis> GetKpisAsync(
        HostScope scope,
        HostDashboardPeriodKind kind,
        DateOnly? month,
        CancellationToken cancellationToken = default);

    Task<HostDashboardKpis> GetKpisAsync(
        HostScope scope,
        HostDashboardQuery query,
        CancellationToken cancellationToken = default);

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

    Task<HostRevenueByProperty> GetRevenueByPropertyAsync(HostScope scope, HostReportQuery query, CancellationToken cancellationToken = default);

    Task<HostOccupancyByProperty> GetOccupancyByPropertyAsync(HostScope scope, HostReportQuery query, CancellationToken cancellationToken = default);

    Task<HostBookingsByChannel> GetBookingsByChannelAsync(HostScope scope, HostReportQuery query, CancellationToken cancellationToken = default);

    Task<HostPerformanceReport> GetPerformanceAsync(HostScope scope, HostPerformanceQuery query, CancellationToken cancellationToken = default);

    Task<HostTrendReport> GetTrendAsync(HostScope scope, HostTrendQuery query, CancellationToken cancellationToken = default);

    Task<IReadOnlyDictionary<Guid, HostDashboardOccupancy>> GetLast30DaysOccupancyAsync(
        IReadOnlyCollection<Guid> propertyIds,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<HostDashboardIcalFeed>> GetIcalFeedsAsync(HostScope scope, CancellationToken cancellationToken = default);

    /// <summary>
    /// The calendars of every active short-rent property of <paramref name="scope"/> in one read (SR-10b,
    /// <c>GET /api/properties/calendar-overview</c>): the feeds of <see cref="GetIcalFeedsAsync"/> (the same code, never the import
    /// URL) gathered by property, the properties that have none, and the token of each property's export link. Three queries whatever
    /// the number of properties; the export link of a property that never asked for it is missing, not created.
    /// </summary>
    Task<HostCalendarOverview> GetCalendarOverviewAsync(HostScope scope, CancellationToken cancellationToken = default);
}

public enum HostDashboardPeriodKind
{
    Month,

    Last30Days,

    Next30Days,

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

public sealed record HostDashboardPeriod(HostDashboardPeriodKind Kind, DateTime From, DateTime To)
{
    public const int Last30DaysLength = 30;

    public const int Next30DaysLength = 30;

    public int Nights => (To - From).Days;

    public HostDashboardPeriod Previous() => this with { From = From.AddDays(-Nights), To = From };

    public static HostDashboardPeriod ForMonth(DateOnly month)
    {
        var from = new DateTime(month.Year, month.Month, 1, 0, 0, 0, DateTimeKind.Utc);
        return new HostDashboardPeriod(HostDashboardPeriodKind.Month, from, from.AddMonths(1));
    }

    public static HostDashboardPeriod ForLast30Days(DateTime todayInRome)
    {
        var to = todayInRome.Date.AddDays(1);
        return new HostDashboardPeriod(HostDashboardPeriodKind.Last30Days, to.AddDays(-Last30DaysLength), to);
    }

    public static HostDashboardPeriod ForNext30Days(DateTime todayInRome)
    {
        var from = todayInRome.Date;
        return new HostDashboardPeriod(HostDashboardPeriodKind.Next30Days, from, from.AddDays(Next30DaysLength));
    }

    public const int MaxCustomDays = 366;

    public const int MinCustomYear = 2000;

    public const int MaxCustomYear = 2100;

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

public sealed record HostDashboardOccupancy(int OccupiedNights, int AvailableNights, int ClosedNights)
{
    public decimal? Rate => AvailableNights == 0 ? null : (decimal)OccupiedNights / AvailableNights;
}

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
    public string BookingCode { get; init; } = string.Empty;

    public BookingSource Source { get; init; }

    public int NumberOfGuests { get; init; }

    public DateTime? ArrivedAt { get; init; }
}

public sealed record HostDashboardStayList(int Count, IReadOnlyList<HostDashboardStay> Items);

public sealed record HostDashboardTodayStays(
    DateTime TodayInRome,
    HostDashboardStayList Arrivals,
    HostDashboardStayList Departures,
    HostDashboardStayList Upcoming);

public sealed record HostDashboardCollected(decimal Amount, int PaymentCount);

public sealed record HostDashboardDirectShare(int DirectStays, int Stays)
{
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
    public HostDashboardCollected? Collected { get; init; }

    public HostDashboardDirectShare DirectShare { get; init; } = new(0, 0);

    /// <summary>The figures of the period before this one; null unless the query asked to compare (SR-03).</summary>
    public HostDashboardFigures? Previous { get; init; }
}

/// <summary>State of one iCal import feed (PC-11) for the dashboard widget; never its URL.</summary>
/// <param name="LastError">Stored error code of the last sync (translated by the web layer).</param>
/// <param name="Deprecated">
/// The Octorate calendar of the feed's channel replaced the import (E02/F02/US01/T03): the feed is no longer synced, so a last try that gets
/// older is expected and not a reason to look at it.
/// </param>
public sealed record HostDashboardIcalFeed(
    Guid FeedId,
    Guid PropertyId,
    string PropertyName,
    ICalFeedChannel Channel,
    string? Label,
    DateTime? LastImportAt,
    PropertyICalImportStatus? LastImportStatus,
    string? LastError,
    bool Deprecated = false);
