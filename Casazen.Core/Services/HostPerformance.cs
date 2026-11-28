using Casazen.Core.Entities;

namespace Casazen.Core.Services;

public enum HostPerformanceGroupBy
{
    None,

    Property,

    Channel,
}

/// <summary>
/// What a performance report asks of <see cref="IHostDashboardService"/> (NF-96): the days of the period, both included, as
/// Europe/Rome calendar dates (checked by <see cref="HostReportRules.CheckPeriod"/> before the read), how it is cut and whether the
/// same days one year earlier are worked out too.
/// </summary>
/// <param name="From">First day of the period, included.</param>
/// <param name="To">Last day of the period, included.</param>
/// <param name="PropertyId">Only this property; null = every property of the scope. It must be one the scope reaches.</param>
/// <param name="GroupBy">The cut of the rows.</param>
/// <param name="CompareYear">Also work out the same calendar days one year earlier (<see cref="HostReportRules.PreviousYear"/>).</param>
/// <param name="PropertyIds">
/// Narrow the properties to these ids <b>inside</b> the caller's scope, never wider: the agency and owner views use it. Null = no extra
/// narrowing; an empty set = nothing.
/// </param>
public sealed record HostPerformanceQuery(
    DateOnly From,
    DateOnly To,
    Guid? PropertyId = null,
    HostPerformanceGroupBy GroupBy = HostPerformanceGroupBy.None,
    bool CompareYear = false,
    IReadOnlyCollection<Guid>? PropertyIds = null);

/// <summary>
/// The figures of one cut of a period (a property, a channel, the whole scope, a week or a month): the Home's nights and the Home's
/// revenue, and what is worked out from them. Revenue is by accrual (<see cref="StayKpiRules"/>): lodging plus cleaning, tourist tax
/// excluded.
/// </summary>
/// <param name="OccupiedNights">Property-nights taken, as the Home counts them (<see cref="PropertyOccupancy"/>).</param>
/// <param name="AvailableNights">Property-nights of the period minus <paramref name="ClosedNights"/>.</param>
/// <param name="ClosedNights">Property-nights closed only by the host's manual blocks.</param>
/// <param name="Revenue">Euros, rounded to the cent (<see cref="StayKpiRules.RevenueOf"/>).</param>
/// <param name="StayCount">Confirmed stays with at least one night in the period.</param>
/// <param name="RevenueNights">
/// The nights of those stays that fall in the period: the nights the revenue is pro rata of, and the denominator of <see cref="Adr"/>.
/// </param>
public sealed record HostPerformanceFigures(
    int OccupiedNights,
    int AvailableNights,
    int ClosedNights,
    decimal Revenue,
    int StayCount,
    int RevenueNights)
{
    public bool HasNights { get; init; } = true;

    public decimal? OccupancyRate => HasNights && AvailableNights > 0 ? (decimal)OccupiedNights / AvailableNights : null;

    public decimal? Adr => HostPerformanceRules.Adr(Revenue, RevenueNights);

    public decimal? RevPar => HasNights ? HostPerformanceRules.RevPar(Revenue, AvailableNights) : null;
}

/// <summary>One row of a performance report: a property, a channel, or the total.</summary>
/// <param name="Key">A stable key, never text to show: the property id, the name of the booking source, or <c>total</c>.</param>
/// <param name="PropertyId">The property of a property row; null for any other.</param>
/// <param name="Label">The name of the property, or the key where there is no name.</param>
/// <param name="Figures">The figures of the period.</param>
/// <param name="PreviousYear">
/// The figures of the same calendar days one year earlier; null when the report did not compare, and when there was nothing to compare
/// with (a property that did not exist yet, a channel without a stay): never zeros made up.
/// </param>
public sealed record HostPerformanceRow(
    string Key,
    Guid? PropertyId,
    string Label,
    HostPerformanceFigures Figures,
    HostPerformanceFigures? PreviousYear);

/// <summary>Performance of the Home (NF-96, <c>GET /api/reports/performance</c>).</summary>
/// <param name="Period">The days of the report.</param>
/// <param name="PreviousYearPeriod">The same calendar days one year earlier; null unless the report compared.</param>
/// <param name="Rows">
/// The rows of the cut: the active short-rent properties of the scope and any other one with revenue in a period (a paused one still has
/// its money), by name; or the channels with a stay, the one with most revenue first; none for <see cref="HostPerformanceGroupBy.None"/>.
/// </param>
/// <param name="Total">
/// The whole scope, worked out over all the stays at once, as the Home does: the revenue and the occupancy the Home gives for the same
/// days. It can differ by a cent from the sum of the rounded rows.
/// </param>
public sealed record HostPerformanceReport(
    HostDashboardPeriod Period,
    HostDashboardPeriod? PreviousYearPeriod,
    IReadOnlyList<HostPerformanceRow> Rows,
    HostPerformanceRow Total);

public enum HostTrendBucketUnit
{
    Auto,

    Week,

    Month,
}

public sealed record HostTrendQuery(
    DateOnly From,
    DateOnly To,
    Guid? PropertyId = null,
    HostPerformanceGroupBy GroupBy = HostPerformanceGroupBy.None,
    bool CompareYear = false,
    IReadOnlyCollection<Guid>? PropertyIds = null,
    HostTrendBucketUnit Bucket = HostTrendBucketUnit.Auto,
    int MaxSeries = HostPerformanceRules.MaxTrendSeries);

/// <summary>A bucket of a trend: a week or a month, cut at the edges of the period, and the days it is compared with.</summary>
/// <param name="Label">A stable key, never text to show: the ISO week (<c>2026-W42</c>) or the month (<c>2026-10</c>).</param>
/// <param name="From">First night of the bucket, midnight UTC of the date.</param>
/// <param name="To">The day after the last night of the bucket.</param>
/// <param name="PreviousYearFrom">First night of the same days one year earlier; null unless the trend compared.</param>
/// <param name="PreviousYearTo">The day after the last night of the same days one year earlier; null unless the trend compared.</param>
public sealed record HostTrendBucket(string Label, DateTime From, DateTime To, DateTime? PreviousYearFrom, DateTime? PreviousYearTo);

public sealed record HostTrendPoint(string Bucket, HostPerformanceFigures Figures);

/// <summary>One line of a trend: the whole scope, a property or a channel (or <c>other</c>, the sum of the rest).</summary>
/// <param name="Key">A stable key: the property id, the name of the booking source, <c>total</c> or <c>other</c>.</param>
/// <param name="Label">The name of the property, or the key where there is no name.</param>
/// <param name="Points">One point for each bucket, in order.</param>
/// <param name="PreviousYear">
/// The same buckets one year earlier, one entry for each bucket, null where there was nothing to compare with; empty unless the trend
/// compared.
/// </param>
public sealed record HostTrendSeries(
    string Key,
    string Label,
    IReadOnlyList<HostTrendPoint> Points,
    IReadOnlyList<HostTrendPoint?> PreviousYear)
{
    public Guid? PropertyId { get; init; }
}

/// <summary>
/// Trend of the Home (NF-96, <c>GET /api/reports/trend</c>): the figures of a performance report for each week or month of the period.
/// A stay across two buckets counts in each for its nights there; the revenue of the buckets of a series adds up to the revenue of the
/// series for the period, to the cent (<see cref="HostOverviewSeriesRules.Distribute"/>).
/// </summary>
/// <param name="Buckets">The weeks or months of the period, in order.</param>
/// <param name="Series">
/// One line for the whole scope, or one for each property or channel: the ones with most revenue first, at most
/// <see cref="HostPerformanceRules.MaxTrendSeries"/>, the rest summed in the <see cref="HostPerformanceRules.OtherSeriesKey"/> one.
/// </param>
public sealed record HostTrendReport(
    HostDashboardPeriod Period,
    HostDashboardPeriod? PreviousYearPeriod,
    IReadOnlyList<HostTrendBucket> Buckets,
    IReadOnlyList<HostTrendSeries> Series)
{
    public HostOverviewSeriesUnit Unit { get; init; }
}

public static class HostPerformanceRules
{
    public const int MaxTrendSeries = 25;

    public const string OtherSeriesKey = "other";

    public const string TotalKey = "total";

    public static decimal? Adr(decimal revenue, decimal revenueNights) =>
        revenueNights <= 0m ? null : Math.Round(revenue / revenueNights, 2, MidpointRounding.AwayFromZero);

    public static decimal? RevPar(decimal revenue, decimal availableNights) =>
        availableNights <= 0m ? null : Math.Round(revenue / availableNights, 2, MidpointRounding.AwayFromZero);

    /// <summary>
    /// How much more (positive) or less (negative) <paramref name="current"/> is than <paramref name="previous"/>, in percent to one
    /// decimal (<see cref="HostOverviewRules.RevenueDeltaPercent"/>); null when either is missing and when <paramref name="previous"/>
    /// is zero: a percentage of nothing says nothing, and there is never a division by zero.
    /// </summary>
    public static decimal? DeltaPercent(decimal? current, decimal? previous) =>
        current is { } now && previous is { } before && before != 0m ? HostOverviewRules.RevenueDeltaPercent(now, before) : null;

    public static decimal? DeltaPoints(decimal? currentRate, decimal? previousRate) =>
        currentRate is { } now && previousRate is { } before
            ? Math.Round((now - before) * 100m, 1, MidpointRounding.AwayFromZero)
            : null;
}
