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
    /// <summary>The name of the report with its base (Italian, the one of the file).</summary>
    public string Title { get; init; } = string.Empty;

    /// <summary>Always <c>accrual</c>: the nights of the confirmed stays, whenever the money came in; occupancy is in nights.</summary>
    public string Basis { get; init; } = "accrual";

    /// <summary>Always EUR: properties have no currency.</summary>
    public string Currency { get; init; } = "EUR";

    public ReportPeriodDto Period { get; init; } = new();

    /// <summary>The owners that hold a part of a property of the report in the period, by name.</summary>
    public IReadOnlyList<OwnerReportRowDto> Rows { get; init; } = [];

    /// <summary>The properties nobody owns in the period, and the part of a property no owner holds.</summary>
    public OwnerReportRowDto Unassigned { get; init; } = new();

    /// <summary>The whole scope, as the Home gives it for the same days (worked out over all the stays at once: it can differ by a cent from the sum of the rows).</summary>
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

/// <summary>An owner (or the properties nobody owns) with its weighted figures.</summary>
public sealed class OwnerReportRowDto
{
    /// <summary>The owner of the registry; null for the row of the properties nobody owns.</summary>
    public Guid? OwnerId { get; init; }

    /// <summary>First and last name; the anonymized label for an owner whose personal data was anonymized; null for the row of the properties nobody owns.</summary>
    public string? DisplayName { get; init; }

    /// <summary>The properties of the report the owner holds a part of in the period.</summary>
    public int PropertyCount { get; init; }

    public WeightedFiguresDto Figures { get; init; } = new();

    /// <summary>The part of the revenue of the whole scope this row has, from 0 to 1; null when the scope had no revenue.</summary>
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

/// <summary>
/// The figures of a cut weighted by an ownership share (<see cref="OwnerPerformanceFigures"/>): the nights and the stays can be
/// fractions of a night or of a stay when a house is shared. ADR, RevPAR and the occupancy are worked out from the weighted values, not
/// by averaging the ones of the properties. Money to the cent, nights and stays to two decimals.
/// </summary>
public sealed class WeightedFiguresDto
{
    /// <summary>Euros, rounded to the cent.</summary>
    public decimal Revenue { get; init; }

    /// <summary><see cref="Revenue"/> in cents of euro, exact.</summary>
    public long RevenueCents { get; init; }

    public decimal OccupiedNights { get; init; }

    public decimal AvailableNights { get; init; }

    public decimal ClosedNights { get; init; }

    /// <summary>Occupied over available nights, from 0 to 1; null when nothing is available.</summary>
    public decimal? OccupancyRate { get; init; }

    public decimal StayCount { get; init; }

    /// <summary>The nights of the stays that fall in the period: what the revenue is pro rata of.</summary>
    public decimal RevenueNights { get; init; }

    /// <summary>Revenue over <see cref="RevenueNights"/>, to the cent; null without a night, never zero.</summary>
    public decimal? Adr { get; init; }

    /// <summary><see cref="Adr"/> in cents of euro.</summary>
    public long? AdrCents { get; init; }

    /// <summary>Revenue over <see cref="AvailableNights"/>, to the cent; null when nothing is available, never zero.</summary>
    public decimal? RevPar { get; init; }

    /// <summary><see cref="RevPar"/> in cents of euro.</summary>
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
