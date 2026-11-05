using Casazen.Core.Services;

namespace Casazen.Web.DTOs;

/// <summary>
/// Performance of the Home for a period (NF-96, <c>GET /api/reports/performance</c>): occupancy, revenue, ADR and RevPAR for the whole
/// scope, per property or per channel, and next to the same calendar days one year earlier when asked. Definitions in
/// <see cref="IHostDashboardService"/> and <c>docs/TECHNICAL.md</c> ("Reports"). Every number is the Home's, worked out by the same
/// code, and the differences from the year before are worked out here on the server, never by the client from rounded numbers.
/// </summary>
public sealed class PerformanceReportDto
{
    /// <summary>The name of the report with its base (Italian, the one of the file).</summary>
    public string Title { get; init; } = string.Empty;

    /// <summary>Always <c>accrual</c>: the nights of the confirmed stays, whenever the money came in; occupancy is in nights.</summary>
    public string Basis { get; init; } = "accrual";

    /// <summary>Always EUR: properties have no currency.</summary>
    public string Currency { get; init; } = "EUR";

    /// <summary>How the rows are cut: <c>none</c> (no rows, the total only), <c>property</c> or <c>channel</c>.</summary>
    public string GroupBy { get; init; } = "none";

    public ReportPeriodDto Period { get; init; } = new();

    /// <summary>The same calendar days one year earlier (29 February becomes 28 February); null unless the report compared.</summary>
    public ReportPeriodDto? ComparePeriod { get; init; }

    /// <summary>A property or channel for each row; a property's row is there when it is active or had revenue in a period.</summary>
    public IReadOnlyList<PerformanceRowDto> Rows { get; init; } = [];

    /// <summary>The whole scope, as the Home gives it for the same days (worked out over all the stays at once: it can differ by a cent from the sum of the rows).</summary>
    public PerformanceRowDto Total { get; init; } = new();

    public static PerformanceReportDto From(HostPerformanceReport report, HostPerformanceGroupBy groupBy, string title)
    {
        ArgumentNullException.ThrowIfNull(report);

        var totalRevenue = report.Total.Figures.Revenue;
        return new PerformanceReportDto
        {
            Title = title,
            GroupBy = PerformanceKeys.GroupBy(groupBy),
            Period = ReportPeriodDto.Of(report.Period),
            ComparePeriod = report.PreviousYearPeriod is { } previous ? ReportPeriodDto.Of(previous) : null,
            Rows = report.Rows.Select(r => PerformanceRowDto.From(r, totalRevenue)).ToList(),
            Total = PerformanceRowDto.From(report.Total, totalRevenue),
        };
    }
}

/// <summary>A property, a channel or the total: its figures, the same days one year earlier and what changed.</summary>
public sealed class PerformanceRowDto
{
    /// <summary>A stable key, never text to show: the property id, the booking source (<c>Direct</c>, <c>Airbnb</c>, ...) or <c>total</c>.</summary>
    public string Key { get; init; } = string.Empty;

    /// <summary>The property of a property row; null for any other.</summary>
    public Guid? PropertyId { get; init; }

    /// <summary>The name of the property; for any other row the same as <see cref="Key"/>.</summary>
    public string Label { get; init; } = string.Empty;

    public PerformanceFiguresDto Figures { get; init; } = new();

    /// <summary>
    /// The same calendar days one year earlier. Null when the report did not compare, and when there was nothing to compare with (a
    /// property that did not exist yet, a channel without a stay): never zeros made up.
    /// </summary>
    public PerformanceFiguresDto? PreviousYear { get; init; }

    /// <summary>How much more (positive) or less (negative) revenue than a year before, in percent to one decimal; null without a year before or with none of its revenue.</summary>
    public decimal? RevenueDeltaPercent { get; init; }

    /// <summary>The occupancy rate minus the one of a year before, in percentage points to one decimal; null when either has no rate.</summary>
    public decimal? OccupancyDeltaPoints { get; init; }

    /// <summary>The ADR against the one of a year before, in percent to one decimal; null when either has none.</summary>
    public decimal? AdrDeltaPercent { get; init; }

    /// <summary>The RevPAR against the one of a year before, in percent to one decimal; null when either has none.</summary>
    public decimal? RevParDeltaPercent { get; init; }

    /// <summary>The part of the revenue of the whole scope this row has, from 0 to 1; null when the scope had no revenue.</summary>
    public decimal? RevenueShare { get; init; }

    public static PerformanceRowDto From(HostPerformanceRow row, decimal totalRevenue)
    {
        var figures = row.Figures;
        var before = row.PreviousYear;
        return new PerformanceRowDto
        {
            Key = row.Key,
            PropertyId = row.PropertyId,
            Label = row.Label,
            Figures = PerformanceFiguresDto.From(figures),
            PreviousYear = before is null ? null : PerformanceFiguresDto.From(before),
            RevenueDeltaPercent = HostPerformanceRules.DeltaPercent(figures.Revenue, before?.Revenue),
            OccupancyDeltaPoints = HostPerformanceRules.DeltaPoints(figures.OccupancyRate, before?.OccupancyRate),
            AdrDeltaPercent = HostPerformanceRules.DeltaPercent(figures.Adr, before?.Adr),
            RevParDeltaPercent = HostPerformanceRules.DeltaPercent(figures.RevPar, before?.RevPar),
            RevenueShare = totalRevenue == 0m ? null : Math.Round(figures.Revenue / totalRevenue, 4, MidpointRounding.AwayFromZero),
        };
    }
}

/// <summary>The figures of one cut of a period. A channel has no nights: they belong to a property, so its night figures, occupancy and RevPAR are null.</summary>
public sealed class PerformanceFiguresDto
{
    /// <summary>Property-nights taken, as the Home counts them; null for a channel.</summary>
    public int? OccupiedNights { get; init; }

    /// <summary>Property-nights of the period minus the closed ones; null for a channel.</summary>
    public int? AvailableNights { get; init; }

    /// <summary>Property-nights closed only by the host's manual blocks; null for a channel.</summary>
    public int? ClosedNights { get; init; }

    /// <summary>Occupied over available nights, from 0 to 1; null when nothing is available and for a channel.</summary>
    public decimal? OccupancyRate { get; init; }

    /// <summary>Euros, rounded to the cent: the confirmed stays pro rata per night, lodging plus cleaning, tourist tax excluded.</summary>
    public decimal Revenue { get; init; }

    /// <summary><see cref="Revenue"/> in cents of euro, exact.</summary>
    public long RevenueCents { get; init; }

    /// <summary>Confirmed stays with at least one night in the period.</summary>
    public int StayCount { get; init; }

    /// <summary>The nights of those stays that fall in the period: what the revenue is pro rata of.</summary>
    public int RevenueNights { get; init; }

    /// <summary>Average daily rate: revenue over <see cref="RevenueNights"/>, to the cent; null without a night, never zero.</summary>
    public decimal? Adr { get; init; }

    /// <summary><see cref="Adr"/> in cents of euro.</summary>
    public long? AdrCents { get; init; }

    /// <summary>Revenue per available night: revenue over <see cref="AvailableNights"/>, to the cent; null when nothing is available and for a channel, never zero.</summary>
    public decimal? RevPar { get; init; }

    /// <summary><see cref="RevPar"/> in cents of euro.</summary>
    public long? RevParCents { get; init; }

    public static PerformanceFiguresDto From(HostPerformanceFigures figures)
    {
        ArgumentNullException.ThrowIfNull(figures);

        var adr = figures.Adr;
        var revPar = figures.RevPar;
        return new PerformanceFiguresDto
        {
            OccupiedNights = figures.HasNights ? figures.OccupiedNights : null,
            AvailableNights = figures.HasNights ? figures.AvailableNights : null,
            ClosedNights = figures.HasNights ? figures.ClosedNights : null,
            OccupancyRate = figures.OccupancyRate,
            Revenue = figures.Revenue,
            RevenueCents = PaymentCashRules.ToCents(figures.Revenue),
            StayCount = figures.StayCount,
            RevenueNights = figures.RevenueNights,
            Adr = adr,
            AdrCents = adr is { } a ? PaymentCashRules.ToCents(a) : null,
            RevPar = revPar,
            RevParCents = revPar is { } r ? PaymentCashRules.ToCents(r) : null,
        };
    }
}

/// <summary>
/// The performance of the Home week by week or month by month (NF-96, <c>GET /api/reports/trend</c>): the figures of
/// <see cref="PerformanceReportDto"/> for each bucket of the period, for the whole scope or for each property or channel, each bucket
/// next to its days one year earlier. A stay across two buckets counts in each for its nights there, and the revenue of the buckets
/// adds up to the revenue of the period, to the cent.
/// </summary>
public sealed class TrendReportDto
{
    /// <summary>The name of the report with its base (Italian, the one of the file).</summary>
    public string Title { get; init; } = string.Empty;

    /// <summary>Always <c>accrual</c>, as <see cref="PerformanceReportDto.Basis"/>.</summary>
    public string Basis { get; init; } = "accrual";

    public string Currency { get; init; } = "EUR";

    /// <summary>How the lines are cut: <c>none</c> (one line, <c>total</c>), <c>property</c> or <c>channel</c>.</summary>
    public string GroupBy { get; init; } = "none";

    /// <summary>The size of the buckets: <c>week</c> (ISO weeks from Monday) or <c>month</c>.</summary>
    public string Unit { get; init; } = "week";

    public ReportPeriodDto Period { get; init; } = new();

    /// <summary>The same calendar days one year earlier; null unless the trend compared.</summary>
    public ReportPeriodDto? ComparePeriod { get; init; }

    public IReadOnlyList<TrendBucketDto> Buckets { get; init; } = [];

    /// <summary>The lines, the one with most revenue first: at most 25, the rest of the properties or channels summed in an <c>other</c> one.</summary>
    public IReadOnlyList<TrendSeriesDto> Series { get; init; } = [];

    public static TrendReportDto From(HostTrendReport report, HostPerformanceGroupBy groupBy, string title)
    {
        ArgumentNullException.ThrowIfNull(report);

        return new TrendReportDto
        {
            Title = title,
            GroupBy = PerformanceKeys.GroupBy(groupBy),
            Unit = report.Unit == HostOverviewSeriesUnit.Month ? "month" : "week",
            Period = ReportPeriodDto.Of(report.Period),
            ComparePeriod = report.PreviousYearPeriod is { } previous ? ReportPeriodDto.Of(previous) : null,
            Buckets = report.Buckets.Select(TrendBucketDto.Of).ToList(),
            Series = report.Series.Select(TrendSeriesDto.From).ToList(),
        };
    }
}

/// <summary>A week or a month of a trend and the days it is compared with.</summary>
public sealed class TrendBucketDto
{
    /// <summary>A stable key, never text to show: the ISO week (<c>2026-W42</c>) or the month (<c>2026-10</c>).</summary>
    public string Label { get; init; } = string.Empty;

    /// <summary>First night of the bucket.</summary>
    public DateOnly From { get; init; }

    /// <summary>Last night of the bucket (included).</summary>
    public DateOnly To { get; init; }

    /// <summary>First night of the same days one year earlier; null unless the trend compared.</summary>
    public DateOnly? PreviousYearFrom { get; init; }

    /// <summary>Last night of the same days one year earlier (included); null unless the trend compared. It is before <see cref="PreviousYearFrom"/> when the bucket has no day left in that year (29 February).</summary>
    public DateOnly? PreviousYearTo { get; init; }

    public static TrendBucketDto Of(HostTrendBucket bucket) => new()
    {
        Label = bucket.Label,
        From = DateOnly.FromDateTime(bucket.From),
        To = DateOnly.FromDateTime(bucket.To.AddDays(-1)),
        PreviousYearFrom = bucket.PreviousYearFrom is { } from ? DateOnly.FromDateTime(from) : null,
        PreviousYearTo = bucket.PreviousYearTo is { } to ? DateOnly.FromDateTime(to.AddDays(-1)) : null,
    };
}

/// <summary>One line of a trend.</summary>
public sealed class TrendSeriesDto
{
    /// <summary>A stable key: the property id, the booking source, <c>total</c> or <c>other</c>.</summary>
    public string Key { get; init; } = string.Empty;

    /// <summary>The property of a property line; null for any other.</summary>
    public Guid? PropertyId { get; init; }

    /// <summary>The name of the property; for any other line the same as <see cref="Key"/>.</summary>
    public string Label { get; init; } = string.Empty;

    /// <summary>One entry for each bucket, in order.</summary>
    public IReadOnlyList<PerformanceFiguresDto> Points { get; init; } = [];

    /// <summary>The same buckets one year earlier, one entry for each, null where there was nothing to compare with; empty unless the trend compared.</summary>
    public IReadOnlyList<PerformanceFiguresDto?> PreviousYear { get; init; } = [];

    public static TrendSeriesDto From(HostTrendSeries series) => new()
    {
        Key = series.Key,
        PropertyId = series.PropertyId,
        Label = series.Label,
        Points = series.Points.Select(p => PerformanceFiguresDto.From(p.Figures)).ToList(),
        PreviousYear = series.PreviousYear.Select(p => p is null ? null : PerformanceFiguresDto.From(p.Figures)).ToList(),
    };
}

/// <summary>The names the API uses for the options of the performance reports.</summary>
public static class PerformanceKeys
{
    /// <summary>The query value (<c>none</c>, <c>property</c>, <c>channel</c>) of a cut.</summary>
    public static string GroupBy(HostPerformanceGroupBy groupBy) => groupBy switch
    {
        HostPerformanceGroupBy.Property => "property",
        HostPerformanceGroupBy.Channel => "channel",
        _ => "none",
    };
}
