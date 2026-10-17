using Casazen.Core.Entities;

namespace Casazen.Core.Services;

/// <summary>
/// What a report asks of <see cref="IHostDashboardService"/> (SR-10): the days of the period, both included, as Europe/Rome calendar
/// dates, and optionally one property (it must be one the scope reaches).
/// </summary>
/// <param name="From">First day of the period, included.</param>
/// <param name="To">Last day of the period, included.</param>
/// <param name="PropertyId">Only this property; null = every property of the scope.</param>
public sealed record HostReportQuery(DateOnly From, DateOnly To, Guid? PropertyId = null);

/// <summary>Whether the days of a report are acceptable (<see cref="HostReportRules.CheckPeriod"/>).</summary>
public enum HostReportPeriodCheck
{
    /// <summary>Accepted.</summary>
    Ok,

    /// <summary>The days are not in order or out of the supported years: a malformed request (400).</summary>
    Invalid,

    /// <summary>The period is longer than <see cref="HostReportRules.MaxMonths"/> calendar months: a rule of the report (422).</summary>
    TooLong,
}

/// <summary>The rules of the periods of the reports (SR-10).</summary>
public static class HostReportRules
{
    /// <summary>The longest period a report covers, in calendar months.</summary>
    public const int MaxMonths = 24;

    /// <summary>
    /// <see cref="HostReportPeriodCheck.Invalid"/> when the last day is before the first or a day is out of the years the dashboard
    /// supports (<see cref="HostDashboardPeriod.MinCustomYear"/> to <see cref="HostDashboardPeriod.MaxCustomYear"/>);
    /// <see cref="HostReportPeriodCheck.TooLong"/> beyond <see cref="MaxMonths"/> calendar months, both days included (1 January 2025 to
    /// 31 December 2026 is 24 months, one more day is not).
    /// </summary>
    public static HostReportPeriodCheck CheckPeriod(DateOnly firstDay, DateOnly lastDay)
    {
        if (lastDay < firstDay
            || firstDay.Year < HostDashboardPeriod.MinCustomYear
            || lastDay.Year > HostDashboardPeriod.MaxCustomYear)
            return HostReportPeriodCheck.Invalid;

        return lastDay > firstDay.AddMonths(MaxMonths).AddDays(-1) ? HostReportPeriodCheck.TooLong : HostReportPeriodCheck.Ok;
    }

    /// <summary>The nights from <paramref name="firstDay"/> to <paramref name="lastDay"/>, both included (already checked).</summary>
    public static HostDashboardPeriod PeriodOf(DateOnly firstDay, DateOnly lastDay)
    {
        var from = firstDay.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
        return new HostDashboardPeriod(HostDashboardPeriodKind.Custom, from, from.AddDays(lastDay.DayNumber - firstDay.DayNumber + 1));
    }
}

/// <summary>Revenue of one property in the period, by accrual: the very rule of the Home, on this property's stays.</summary>
/// <param name="Amount">Euros, rounded to the cent (<see cref="StayKpiRules.RevenueOf"/>).</param>
/// <param name="StayCount">Confirmed stays with at least one night in the period.</param>
/// <param name="Nights">Their nights that fall in the period.</param>
public sealed record HostRevenueByPropertyRow(Guid PropertyId, string PropertyName, decimal Amount, int StayCount, int Nights);

/// <summary>Revenue by property (SR-10, <c>GET /api/reports/revenue-by-property</c>).</summary>
/// <param name="Rows">
/// The active short-rent properties of the scope and any other property with revenue in the period (a paused one still has its
/// money), by name.
/// </param>
/// <param name="Amount">
/// The revenue of the period worked out over all the stays at once, as the Home does (so the two numbers are the same one); it can
/// differ by a cent from the sum of the rounded rows.
/// </param>
public sealed record HostRevenueByProperty(
    HostDashboardPeriod Period,
    IReadOnlyList<HostRevenueByPropertyRow> Rows,
    decimal Amount,
    int StayCount,
    int Nights);

/// <summary>Occupancy of one active short-rent property in the period.</summary>
public sealed record HostOccupancyByPropertyRow(Guid PropertyId, string PropertyName, HostDashboardOccupancy Occupancy);

/// <summary>Occupancy by property (SR-10, <c>GET /api/reports/occupancy-by-property</c>).</summary>
/// <param name="Rows">The active short-rent properties of the scope, by name.</param>
/// <param name="Total">The occupancy of the Home over the same properties: the sums of the rows.</param>
public sealed record HostOccupancyByProperty(
    HostDashboardPeriod Period,
    IReadOnlyList<HostOccupancyByPropertyRow> Rows,
    HostDashboardOccupancy Total);

/// <summary>The confirmed stays that came from one channel (<see cref="Booking.Source"/>) in the period.</summary>
public sealed record HostBookingsByChannelRow(BookingSource Source, int BookingCount, int Nights, decimal Revenue);

/// <summary>Bookings by channel (SR-10, <c>GET /api/reports/bookings-by-channel</c>).</summary>
/// <param name="Rows">The channels that have at least one stay in the period, the one with most stays first.</param>
/// <param name="Revenue">The revenue of the period over all the stays at once, as the Home does (see <see cref="HostRevenueByProperty"/>).</param>
public sealed record HostBookingsByChannel(
    HostDashboardPeriod Period,
    IReadOnlyList<HostBookingsByChannelRow> Rows,
    int BookingCount,
    int Nights,
    decimal Revenue);
