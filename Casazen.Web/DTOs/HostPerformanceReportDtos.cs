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
    public string Title { get; init; } = string.Empty;

    public string Basis { get; init; } = "accrual";

    public string Currency { get; init; } = "EUR";

    public string GroupBy { get; init; } = "none";

    public ReportPeriodDto Period { get; init; } = new();

    /// <summary>The same calendar days one year earlier (29 February becomes 28 February); null unless the report compared.</summary>
    public ReportPeriodDto? ComparePeriod { get; init; }

    public IReadOnlyList<PerformanceRowDto> Rows { get; init; } = [];

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

public sealed class PerformanceRowDto
{
    /// <summary>A stable key, never text to show: the property id, the booking source (<c>Direct</c>, <c>Airbnb</c>, ...) or <c>total</c>.</summary>
    public string Key { get; init; } = string.Empty;

    public Guid? PropertyId { get; init; }

    public string Label { get; init; } = string.Empty;

    public PerformanceFiguresDto Figures { get; init; } = new();

    public PerformanceFiguresDto? PreviousYear { get; init; }

    public decimal? RevenueDeltaPercent { get; init; }

    public decimal? OccupancyDeltaPoints { get; init; }

    public decimal? AdrDeltaPercent { get; init; }

    public decimal? RevParDeltaPercent { get; init; }

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

public sealed class PerformanceFiguresDto
{
    public int? OccupiedNights { get; init; }

    public int? AvailableNights { get; init; }

    public int? ClosedNights { get; init; }

    public decimal? OccupancyRate { get; init; }

    public decimal Revenue { get; init; }

    public long RevenueCents { get; init; }

    public int StayCount { get; init; }

    public int RevenueNights { get; init; }

    public decimal? Adr { get; init; }

    public long? AdrCents { get; init; }

    public decimal? RevPar { get; init; }

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

public sealed class TrendReportDto
{
    public string Title { get; init; } = string.Empty;

    public string Basis { get; init; } = "accrual";

    public string Currency { get; init; } = "EUR";

    public string GroupBy { get; init; } = "none";

    public string Unit { get; init; } = "week";

    public ReportPeriodDto Period { get; init; } = new();

    /// <summary>The same calendar days one year earlier; null unless the trend compared.</summary>
    public ReportPeriodDto? ComparePeriod { get; init; }

    public IReadOnlyList<TrendBucketDto> Buckets { get; init; } = [];

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

public sealed class TrendBucketDto
{
    public string Label { get; init; } = string.Empty;

    public DateOnly From { get; init; }

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

public sealed class TrendSeriesDto
{
    public string Key { get; init; } = string.Empty;

    public Guid? PropertyId { get; init; }

    public string Label { get; init; } = string.Empty;

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

public static class PerformanceKeys
{
    public static string GroupBy(HostPerformanceGroupBy groupBy) => groupBy switch
    {
        HostPerformanceGroupBy.Property => "property",
        HostPerformanceGroupBy.Channel => "channel",
        _ => "none",
    };
}
