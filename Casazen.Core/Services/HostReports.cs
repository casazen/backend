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

    /// <summary>
    /// A trend is cut by week up to this many days, by month beyond (NF-96): thirteen weeks is a chart one reads, a year of weeks is not.
    /// The Home's chart uses the same rule with a shorter limit (<see cref="HostOverviewSeriesRules.MaxWeeklyDays"/>).
    /// </summary>
    public const int MaxWeeklyTrendDays = 92;

    /// <summary>
    /// The unit of the buckets of a trend: the one asked for, or, for <see cref="HostTrendBucketUnit.Auto"/>, weeks up to
    /// <see cref="MaxWeeklyTrendDays"/> days and months beyond, through the Home's own rule (<see cref="HostOverviewSeriesRules.UnitFor(HostDashboardPeriod, int)"/>).
    /// </summary>
    public static HostOverviewSeriesUnit TrendUnitFor(HostDashboardPeriod period, HostTrendBucketUnit requested)
    {
        ArgumentNullException.ThrowIfNull(period);

        return requested switch
        {
            HostTrendBucketUnit.Week => HostOverviewSeriesUnit.Week,
            HostTrendBucketUnit.Month => HostOverviewSeriesUnit.Month,
            _ => HostOverviewSeriesRules.UnitFor(period, MaxWeeklyTrendDays),
        };
    }

    /// <summary>
    /// The same calendar days one year earlier (NF-96): <c>2026-10-01</c> to <c>2026-10-31</c> becomes <c>2025-10-01</c> to
    /// <c>2025-10-31</c>. 29 February becomes 28 February of a year that has no 29th, so the previous period is one day shorter when
    /// the days span a leap day (1 to 29 February 2028 are 29 days, 1 to 28 February 2027 are 28).
    /// </summary>
    public static (DateOnly First, DateOnly Last) PreviousYear(DateOnly firstDay, DateOnly lastDay) =>
        (firstDay.AddYears(-1), lastDay.AddYears(-1));

    /// <summary>The nights of the same calendar days one year earlier (<see cref="PreviousYear"/>), as <see cref="PeriodOf"/> gives them.</summary>
    public static HostDashboardPeriod PreviousYearPeriodOf(DateOnly firstDay, DateOnly lastDay)
    {
        var (first, last) = PreviousYear(firstDay, lastDay);
        return PeriodOf(first, last);
    }

    /// <summary>
    /// The days of each bucket of a trend moved one year back, to be compared with it. Each one ends on the very day one year before the
    /// day its bucket ends and starts the day after the one before it ended (the first on the same day one year earlier), so the buckets
    /// of the year before cover its period exactly and none of its days is in two of them, even when 29 February makes two days of
    /// this year the same day of the year before: the bucket that would have nothing left is empty (its <c>To</c> is its <c>From</c>).
    /// </summary>
    /// <param name="buckets">The buckets of the period, in order, each from its first night to the day after its last.</param>
    public static IReadOnlyList<(DateTime From, DateTime To)> PreviousYearBuckets(IReadOnlyList<(DateTime From, DateTime To)> buckets)
    {
        ArgumentNullException.ThrowIfNull(buckets);

        var moved = new List<(DateTime From, DateTime To)>(buckets.Count);
        DateTime? endedOn = null;
        foreach (var bucket in buckets)
        {
            var from = endedOn ?? bucket.From.AddYears(-1);
            var to = bucket.To.AddDays(-1).AddYears(-1).AddDays(1);
            if (to < from)
                to = from;

            moved.Add((from, to));
            endedOn = to;
        }

        return moved;
    }

    /// <summary>
    /// The buckets of a trend over <paramref name="period"/> (NF-96): the weeks or months of the Home's chart
    /// (<see cref="HostOverviewSeriesRules.BucketsOf"/>), cut at the edges of the period, each with the same days one year earlier when
    /// <paramref name="compareYear"/> (<see cref="PreviousYearBuckets"/>). The one place that cuts them: the reports of the host and
    /// the owner portal read their buckets here, so the lines of both answer the same weeks.
    /// </summary>
    public static (HostOverviewSeriesUnit Unit, IReadOnlyList<HostTrendBucket> Buckets) TrendBucketsOf(
        HostDashboardPeriod period, HostTrendBucketUnit requested, bool compareYear)
    {
        ArgumentNullException.ThrowIfNull(period);

        var unit = TrendUnitFor(period, requested);
        var spans = HostOverviewSeriesRules.BucketsOf(period, unit);
        var previous = compareYear ? PreviousYearBuckets(spans.Select(span => (span.From, span.To)).ToList()) : null;
        var buckets = spans
            .Select((span, index) => new HostTrendBucket(span.Label, span.From, span.To, previous?[index].From, previous?[index].To))
            .ToList();
        return (unit, buckets);
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
