using Casazen.Core.Entities;
using Casazen.Core.Services;

namespace Casazen.Web.DTOs.OwnerPortal;

/// <summary>
/// Property summary for owner portal (ADR-011-R8): basic property info with occupancy and revenue.
/// Guest data is masked per ADR-011-R3.
/// </summary>
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

/// <summary>
/// Booking summary with guest data masked (ADR-011-R3): names NULL, emails NULL, phones NULL.
/// Only dates, channel, and amounts visible.
/// </summary>
public class OwnerPortalBookingSummaryDto
{
    public Guid BookingId { get; set; }
    public DateTime CheckInDate { get; set; }
    public DateTime CheckOutDate { get; set; }
    public string Channel { get; set; } = string.Empty;
    public decimal TotalAmount { get; set; }
    public BookingStatus Status { get; set; }
}

/// <summary>
/// Monthly statement summary for owner portal (ADR-011-R2).
/// Only published statements are visible.
/// </summary>
public class OwnerPortalStatementDto
{
    public Guid StatementId { get; set; }
    public Guid PropertyId { get; set; }
    public string PropertyName { get; set; } = string.Empty;
    public int Month { get; set; }
    public int Year { get; set; }
    public decimal TotalRevenue { get; set; }
    public decimal AgencyCommission { get; set; }
    public decimal NetAmount { get; set; }
    public DateTime PublishedAt { get; set; }
}

/// <summary>
/// Invoice summary for owner portal (ADR-011-R2).
/// Invoices received by the owner from the agency.
/// </summary>
public class OwnerPortalInvoiceDto
{
    public Guid InvoiceId { get; set; }
    public string InvoiceNumber { get; set; } = string.Empty;
    public DateTime InvoiceDate { get; set; }
    public decimal Amount { get; set; }
    public string Description { get; set; } = string.Empty;
    public bool IsPaid { get; set; }
}

/// <summary>
/// Withholding tax summary for owner portal (ADR-011-R2).
/// Aggregated withholding information for tax purposes.
/// </summary>
public class OwnerPortalWithholdingDto
{
    public int Year { get; set; }
    public decimal TotalWithheld { get; set; }
    public IReadOnlyList<OwnerPortalWithholdingDetailDto> Details { get; set; } = [];
}

/// <summary>
/// Detailed withholding entry.
/// </summary>
public class OwnerPortalWithholdingDetailDto
{
    public Guid PropertyId { get; set; }
    public string PropertyName { get; set; } = string.Empty;
    public int Month { get; set; }
    public decimal AmountWithheld { get; set; }
}

/// <summary>
/// The owner's properties on the Home's numbers (NF-96, <c>GET /api/owner-portal/overview</c>): occupancy, revenue, ADR and RevPAR of
/// the days asked and, with <c>compare=previousYear</c>, of the same calendar days one year earlier, and what changed (worked out by the
/// server). The figures are the Home's, worked out by the same code, and <b>weighted by the share the owner holds</b> of each property,
/// so the owners of a co-owned property together are the Home's figure for it. Definitions in <c>docs/TECHNICAL.md</c> ("Owner portal").
/// No guest and nothing of any other owner: only the owner's own properties by name, and numbers.
/// </summary>
public sealed class OwnerPortalOverviewDto
{
    /// <summary>The name of the report with its base (Italian).</summary>
    public string Title { get; init; } = string.Empty;

    /// <summary>Always <c>accrual</c>: the nights of the confirmed stays, whenever the money came in; occupancy is in nights.</summary>
    public string Basis { get; init; } = "accrual";

    /// <summary>Always EUR: properties have no currency.</summary>
    public string Currency { get; init; } = "EUR";

    /// <summary>How the rows are cut: <c>none</c> (no rows, the total only) or <c>property</c>.</summary>
    public string GroupBy { get; init; } = "none";

    public ReportPeriodDto Period { get; init; } = new();

    /// <summary>The same calendar days one year earlier (29 February becomes 28 February); null unless the overview compared.</summary>
    public ReportPeriodDto? ComparePeriod { get; init; }

    /// <summary>A row for each property of the owner that is active or had revenue; none unless cut by property.</summary>
    public IReadOnlyList<OwnerPortalRowDto> Rows { get; init; } = [];

    /// <summary>All the owner's properties together.</summary>
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

/// <summary>A property of the owner, or the total: its weighted figures, the same days one year earlier and what changed.</summary>
public sealed class OwnerPortalRowDto
{
    /// <summary>A stable key, never text to show: the property id, or <c>total</c>.</summary>
    public string Key { get; init; } = string.Empty;

    /// <summary>The property of a property row; null for the total.</summary>
    public Guid? PropertyId { get; init; }

    /// <summary>The name of the property; <c>total</c> for the total.</summary>
    public string Label { get; init; } = string.Empty;

    public WeightedFiguresDto Figures { get; init; } = new();

    /// <summary>The same calendar days one year earlier; null when the overview did not compare and when there was nothing to compare with.</summary>
    public WeightedFiguresDto? PreviousYear { get; init; }

    /// <summary>How much more (positive) or less (negative) revenue than a year before, in percent to one decimal; null without a year before or with none of its revenue.</summary>
    public decimal? RevenueDeltaPercent { get; init; }

    /// <summary>The occupancy rate minus the one of a year before, in percentage points to one decimal; null when either has no rate.</summary>
    public decimal? OccupancyDeltaPoints { get; init; }

    /// <summary>The ADR against the one of a year before, in percent to one decimal; null when either has none.</summary>
    public decimal? AdrDeltaPercent { get; init; }

    /// <summary>The RevPAR against the one of a year before, in percent to one decimal; null when either has none.</summary>
    public decimal? RevParDeltaPercent { get; init; }

    /// <summary>The part of the owner's revenue this row has, from 0 to 1; null when the owner had no revenue.</summary>
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

/// <summary>
/// The owner's properties week by week or month by month (NF-96, <c>GET /api/owner-portal/trend</c>): the figures of
/// <see cref="OwnerPortalOverviewDto"/> for each bucket of the period (the same weeks or months as the host's trend), for all the
/// properties together or one line for each (at most 25, the rest in <c>other</c>), each bucket next to its days one year earlier.
/// </summary>
public sealed class OwnerPortalTrendDto
{
    public string Title { get; init; } = string.Empty;

    public string Basis { get; init; } = "accrual";

    public string Currency { get; init; } = "EUR";

    /// <summary>How the lines are cut: <c>none</c> (one line, <c>total</c>) or <c>property</c>.</summary>
    public string GroupBy { get; init; } = "none";

    /// <summary>The size of the buckets: <c>week</c> (ISO weeks from Monday) or <c>month</c>.</summary>
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

/// <summary>One line of the owner's trend.</summary>
public sealed class OwnerPortalSeriesDto
{
    /// <summary>A stable key: the property id, <c>total</c> or <c>other</c>.</summary>
    public string Key { get; init; } = string.Empty;

    /// <summary>The property of a property line; null for any other.</summary>
    public Guid? PropertyId { get; init; }

    public string Label { get; init; } = string.Empty;

    /// <summary>One entry for each bucket, in order.</summary>
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
