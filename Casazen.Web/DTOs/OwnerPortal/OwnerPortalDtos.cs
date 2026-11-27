using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;

namespace Casazen.Web.DTOs.OwnerPortal;

public class OwnerPortalPropertyDto
{
    public Guid PropertyId { get; set; }
    public string PropertyName { get; set; } = string.Empty;
    public string Address { get; set; } = string.Empty;
    public int TotalBookings { get; set; }
    public decimal TotalRevenue { get; set; }
    public double OccupancyRate { get; set; }
    public IReadOnlyList<OwnerPortalBookingSummaryDto> RecentBookings { get; set; } = [];
}

public class OwnerPortalBookingSummaryDto
{
    public Guid BookingId { get; set; }
    public DateTime CheckInDate { get; set; }
    public DateTime CheckOutDate { get; set; }
    public string Channel { get; set; } = string.Empty;
    public decimal TotalAmount { get; set; }
    public BookingStatus Status { get; set; }
}

public class OwnerPortalWithholdingDto
{
    public int Year { get; set; }

    public string Currency { get; set; } = "EUR";

    public decimal TotalWithheld { get; set; }

    public IReadOnlyList<OwnerPortalWithholdingDetailDto> Details { get; set; } = [];

    public IReadOnlyList<OwnerPortalCuDto> CuStatements { get; set; } = [];
}

public class OwnerPortalWithholdingDetailDto
{
    public Guid AgencyId { get; set; }
    public string AgencyName { get; set; } = string.Empty;
    public Guid PropertyId { get; set; }
    public string PropertyName { get; set; } = string.Empty;
    public int Month { get; set; }
    public decimal GrossBase { get; set; }
    public decimal AmountWithheld { get; set; }
}

/// <summary>
/// The Certificazione Unica data of the owner for the year as the agency stored it: its state, the base and the withholding in euro, the F24 tax code and how the record was made
/// (<c>owner</c>, <c>signatory</c> or <c>share</c>). Never the recipient data, the stays or the co-owners: those are the agency's.
/// </summary>
public class OwnerPortalCuDto
{
    public Guid AgencyId { get; set; }
    public string AgencyName { get; set; } = string.Empty;
    public int Year { get; set; }
    public TaxFilingState State { get; set; }
    public DateTime GeneratedAt { get; set; }
    public decimal GrossBase { get; set; }
    public decimal Withheld { get; set; }
    public string? TaxCode { get; set; }
    public string Attribution { get; set; } = string.Empty;
}

public sealed class OwnerPortalOverviewDto
{
    public string Title { get; init; } = string.Empty;

    public string Basis { get; init; } = "accrual";

    public string Currency { get; init; } = "EUR";

    public string GroupBy { get; init; } = "none";

    public ReportPeriodDto Period { get; init; } = new();

    /// <summary>The same calendar days one year earlier (29 February becomes 28 February); null unless the overview compared.</summary>
    public ReportPeriodDto? ComparePeriod { get; init; }

    /// <summary>A row for each property of the owner that is active or had revenue; none unless cut by property.</summary>
    public IReadOnlyList<OwnerPortalRowDto> Rows { get; init; } = [];

    public OwnerPortalRowDto Total { get; init; } = new();

    public static OwnerPortalOverviewDto From(OwnerPortalOverview overview, HostPerformanceGroupBy groupBy, string title)
    {
        ArgumentNullException.ThrowIfNull(overview);

        var totalRevenue = overview.Total.Figures.Revenue;
        return new OwnerPortalOverviewDto
        {
            Title = title,
            GroupBy = PerformanceKeys.GroupBy(groupBy),
            Period = ReportPeriodDto.Of(overview.Period),
            ComparePeriod = overview.PreviousYearPeriod is { } previous ? ReportPeriodDto.Of(previous) : null,
            Rows = overview.Rows.Select(r => OwnerPortalRowDto.From(r, totalRevenue)).ToList(),
            Total = OwnerPortalRowDto.From(overview.Total, totalRevenue),
        };
    }
}

public sealed class OwnerPortalRowDto
{
    public string Key { get; init; } = string.Empty;

    public Guid? PropertyId { get; init; }

    public string Label { get; init; } = string.Empty;

    public WeightedFiguresDto Figures { get; init; } = new();

    public WeightedFiguresDto? PreviousYear { get; init; }

    public decimal? RevenueDeltaPercent { get; init; }

    public decimal? OccupancyDeltaPoints { get; init; }

    public decimal? AdrDeltaPercent { get; init; }

    public decimal? RevParDeltaPercent { get; init; }

    public decimal? RevenueShare { get; init; }

    public static OwnerPortalRowDto From(OwnerPortalRow row, decimal totalRevenue)
    {
        var figures = WeightedFiguresDto.From(row.Figures);
        var before = row.PreviousYear is null ? null : WeightedFiguresDto.From(row.PreviousYear);
        var whole = Math.Round(totalRevenue, 2, MidpointRounding.AwayFromZero);
        return new OwnerPortalRowDto
        {
            Key = row.Key,
            PropertyId = row.PropertyId,
            Label = row.Label,
            Figures = figures,
            PreviousYear = before,
            RevenueDeltaPercent = HostPerformanceRules.DeltaPercent(figures.Revenue, before?.Revenue),
            OccupancyDeltaPoints = HostPerformanceRules.DeltaPoints(figures.OccupancyRate, before?.OccupancyRate),
            AdrDeltaPercent = HostPerformanceRules.DeltaPercent(figures.Adr, before?.Adr),
            RevParDeltaPercent = HostPerformanceRules.DeltaPercent(figures.RevPar, before?.RevPar),
            RevenueShare = whole == 0m ? null : Math.Round(figures.Revenue / whole, 4, MidpointRounding.AwayFromZero),
        };
    }
}

public sealed class OwnerPortalTrendDto
{
    public string Title { get; init; } = string.Empty;

    public string Basis { get; init; } = "accrual";

    public string Currency { get; init; } = "EUR";

    public string GroupBy { get; init; } = "none";

    public string Unit { get; init; } = "week";

    public ReportPeriodDto Period { get; init; } = new();

    public ReportPeriodDto? ComparePeriod { get; init; }

    public IReadOnlyList<TrendBucketDto> Buckets { get; init; } = [];

    public IReadOnlyList<OwnerPortalSeriesDto> Series { get; init; } = [];

    public static OwnerPortalTrendDto From(OwnerPortalTrend trend, HostPerformanceGroupBy groupBy, string title)
    {
        ArgumentNullException.ThrowIfNull(trend);

        return new OwnerPortalTrendDto
        {
            Title = title,
            GroupBy = PerformanceKeys.GroupBy(groupBy),
            Unit = trend.Unit == HostOverviewSeriesUnit.Month ? "month" : "week",
            Period = ReportPeriodDto.Of(trend.Period),
            ComparePeriod = trend.PreviousYearPeriod is { } previous ? ReportPeriodDto.Of(previous) : null,
            Buckets = trend.Buckets.Select(TrendBucketDto.Of).ToList(),
            Series = trend.Series.Select(OwnerPortalSeriesDto.From).ToList(),
        };
    }
}

public sealed class OwnerPortalSeriesDto
{
    public string Key { get; init; } = string.Empty;

    public Guid? PropertyId { get; init; }

    public string Label { get; init; } = string.Empty;

    public IReadOnlyList<WeightedFiguresDto> Points { get; init; } = [];

    /// <summary>The same buckets one year earlier, null where there was nothing to compare with; empty unless the trend compared.</summary>
    public IReadOnlyList<WeightedFiguresDto?> PreviousYear { get; init; } = [];

    public static OwnerPortalSeriesDto From(OwnerPortalSeries series) => new()
    {
        Key = series.Key,
        PropertyId = series.PropertyId,
        Label = series.Label,
        Points = series.Points.Select(WeightedFiguresDto.From).ToList(),
        PreviousYear = series.PreviousYear.Select(p => p is null ? null : WeightedFiguresDto.From(p)).ToList(),
    };
}
