using Casazen.Core.Services;

namespace Casazen.Web.DTOs;

/// <summary>
/// The performance of an agency's properties cut by property owner (NF-96, <c>GET /api/reports/by-owner</c>): each owner counts the
/// share it holds of each property in the days of the report, so a co-owned property counts once in the total and the owners together
/// with the properties nobody owns add up to the total the Home gives for the same days. Definitions in
/// <see cref="IOwnerReportService"/> and <c>docs/TECHNICAL.md</c> ("Reports"). Nothing personal about an owner but its name: never a
/// fiscal code, an IBAN, an e-mail or a phone.
/// </summary>
public sealed class OwnerReportDto
{
    public string Title { get; init; } = string.Empty;

    public string Basis { get; init; } = "accrual";

    public string Currency { get; init; } = "EUR";

    public ReportPeriodDto Period { get; init; } = new();

    public IReadOnlyList<OwnerReportRowDto> Rows { get; init; } = [];

    public OwnerReportRowDto Unassigned { get; init; } = new();

    public WeightedFiguresDto Total { get; init; } = new();

    public static OwnerReportDto From(OwnerPerformanceReport report, string title, string anonymizedLabel)
    {
        ArgumentNullException.ThrowIfNull(report);

        var totalRevenue = report.Total.Revenue;
        return new OwnerReportDto
        {
            Title = title,
            Period = ReportPeriodDto.Of(report.Period),
            Rows = report.Rows.Select(r => OwnerReportRowDto.From(r, anonymizedLabel, totalRevenue)).ToList(),
            Unassigned = OwnerReportRowDto.From(report.Unassigned, anonymizedLabel, totalRevenue),
            Total = WeightedFiguresDto.From(report.Total),
        };
    }
}

public sealed class OwnerReportRowDto
{
    public Guid? OwnerId { get; init; }

    public string? DisplayName { get; init; }

    public int PropertyCount { get; init; }

    public WeightedFiguresDto Figures { get; init; } = new();

    public decimal? RevenueShare { get; init; }

    public static OwnerReportRowDto From(OwnerPerformanceRow row, string anonymizedLabel, decimal totalRevenue) => new()
    {
        OwnerId = row.OwnerId,
        DisplayName = row.IsAnonymized ? anonymizedLabel : row.DisplayName,
        PropertyCount = row.PropertyCount,
        Figures = WeightedFiguresDto.From(row.Figures),
        RevenueShare = totalRevenue == 0m ? null : Math.Round(row.Figures.Revenue / totalRevenue, 4, MidpointRounding.AwayFromZero),
    };
}

public sealed class WeightedFiguresDto
{
    public decimal Revenue { get; init; }

    public long RevenueCents { get; init; }

    public decimal OccupiedNights { get; init; }

    public decimal AvailableNights { get; init; }

    public decimal ClosedNights { get; init; }

    public decimal? OccupancyRate { get; init; }

    public decimal StayCount { get; init; }

    public decimal RevenueNights { get; init; }

    public decimal? Adr { get; init; }

    public long? AdrCents { get; init; }

    public decimal? RevPar { get; init; }

    public long? RevParCents { get; init; }

    public static WeightedFiguresDto From(OwnerPerformanceFigures figures)
    {
        ArgumentNullException.ThrowIfNull(figures);

        var revenue = Money(figures.Revenue);
        var adr = figures.Adr;
        var revPar = figures.RevPar;
        return new WeightedFiguresDto
        {
            Revenue = revenue,
            RevenueCents = PaymentCashRules.ToCents(revenue),
            OccupiedNights = Nights(figures.OccupiedNights),
            AvailableNights = Nights(figures.AvailableNights),
            ClosedNights = Nights(figures.ClosedNights),
            OccupancyRate = figures.OccupancyRate,
            StayCount = Nights(figures.StayCount),
            RevenueNights = Nights(figures.RevenueNights),
            Adr = adr,
            AdrCents = adr is { } a ? PaymentCashRules.ToCents(a) : null,
            RevPar = revPar,
            RevParCents = revPar is { } r ? PaymentCashRules.ToCents(r) : null,
        };
    }

    private static decimal Money(decimal euros) => Math.Round(euros, 2, MidpointRounding.AwayFromZero);

    private static decimal Nights(decimal nights) => Math.Round(nights, 2, MidpointRounding.AwayFromZero);
}
