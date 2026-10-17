using Casazen.Core.Services;

namespace Casazen.Web.DTOs;

/// <summary>
/// The days a report covers, both included, as Europe/Rome calendar dates (<c>yyyy-MM-dd</c>). The reports of SR-10
/// (<c>GET /api/reports/*</c>) are money and occupancy of the Home cut by property or channel: definitions in
/// <see cref="IHostDashboardService"/> and <c>docs/TECHNICAL.md</c> ("Reports"). Every one says its <c>basis</c> and carries a
/// <c>title</c> that names it, so no number is without a label.
/// </summary>
public sealed class ReportPeriodDto
{
    /// <summary>First night of the period.</summary>
    public DateOnly From { get; init; }

    /// <summary>Last night of the period (included).</summary>
    public DateOnly To { get; init; }

    public int Nights { get; init; }

    public static ReportPeriodDto Of(HostDashboardPeriod period) => new()
    {
        From = DateOnly.FromDateTime(period.From),
        To = DateOnly.FromDateTime(period.To.AddDays(-1)),
        Nights = period.Nights,
    };
}

/// <summary>Revenue by property: the revenue of the Home, by accrual, cut by property.</summary>
public sealed class RevenueByPropertyReportDto
{
    /// <summary>The name of the report with its base (Italian, the one of the file).</summary>
    public string Title { get; init; } = string.Empty;

    /// <summary>Always <c>accrual</c>: the nights of the confirmed stays, whenever the money came in (not the cash of Payments).</summary>
    public string Basis { get; init; } = "accrual";

    /// <summary>Always EUR: properties have no currency.</summary>
    public string Currency { get; init; } = "EUR";

    public ReportPeriodDto Period { get; init; } = new();

    /// <summary>The active short-rent properties of the caller's scope, and any other one with revenue in the period, by name.</summary>
    public IReadOnlyList<RevenueByPropertyRowDto> Rows { get; init; } = [];

    /// <summary>The revenue the Home gives for the same days (worked out over all the stays at once: it can differ by a cent from the sum of the rows).</summary>
    public ReportRevenueTotalDto Total { get; init; } = new();

    public static RevenueByPropertyReportDto From(HostRevenueByProperty report, string title) => new()
    {
        Title = title,
        Period = ReportPeriodDto.Of(report.Period),
        Rows = report.Rows.Select(RevenueByPropertyRowDto.From).ToList(),
        Total = new ReportRevenueTotalDto
        {
            Amount = report.Amount,
            AmountCents = PaymentCashRules.ToCents(report.Amount),
            StayCount = report.StayCount,
            Nights = report.Nights,
        },
    };
}

public sealed class RevenueByPropertyRowDto
{
    public Guid PropertyId { get; init; }

    public string PropertyName { get; init; } = string.Empty;

    /// <summary>Euros, rounded to the cent.</summary>
    public decimal Amount { get; init; }

    /// <summary><see cref="Amount"/> in cents of euro, exact.</summary>
    public long AmountCents { get; init; }

    /// <summary>Confirmed stays with at least one night in the period.</summary>
    public int StayCount { get; init; }

    /// <summary>Their nights that fall in the period.</summary>
    public int Nights { get; init; }

    public static RevenueByPropertyRowDto From(HostRevenueByPropertyRow row) => new()
    {
        PropertyId = row.PropertyId,
        PropertyName = row.PropertyName,
        Amount = row.Amount,
        AmountCents = PaymentCashRules.ToCents(row.Amount),
        StayCount = row.StayCount,
        Nights = row.Nights,
    };
}

/// <summary>The revenue of a period and the stays and nights it is made of.</summary>
public sealed class ReportRevenueTotalDto
{
    /// <summary>Euros, rounded to the cent.</summary>
    public decimal Amount { get; init; }

    /// <summary><see cref="Amount"/> in cents of euro, exact.</summary>
    public long AmountCents { get; init; }

    public int StayCount { get; init; }

    public int Nights { get; init; }
}

/// <summary>Occupancy by property: the nights of the Home cut by property.</summary>
public sealed class OccupancyByPropertyReportDto
{
    public string Title { get; init; } = string.Empty;

    /// <summary>Always <c>nights</c>: occupied nights over available nights, as the Home counts them.</summary>
    public string Basis { get; init; } = "nights";

    public ReportPeriodDto Period { get; init; } = new();

    /// <summary>The active short-rent properties of the caller's scope, by name.</summary>
    public IReadOnlyList<OccupancyByPropertyRowDto> Rows { get; init; } = [];

    /// <summary>The occupancy the Home gives for the same days: the sums of the rows.</summary>
    public HostDashboardOccupancyDto Total { get; init; } = new();

    public static OccupancyByPropertyReportDto From(HostOccupancyByProperty report, string title) => new()
    {
        Title = title,
        Period = ReportPeriodDto.Of(report.Period),
        Rows = report.Rows
            .Select(r => new OccupancyByPropertyRowDto
            {
                PropertyId = r.PropertyId,
                PropertyName = r.PropertyName,
                Occupancy = HostDashboardOccupancyDto.From(r.Occupancy),
            })
            .ToList(),
        Total = HostDashboardOccupancyDto.From(report.Total),
    };
}

public sealed class OccupancyByPropertyRowDto
{
    public Guid PropertyId { get; init; }

    public string PropertyName { get; init; } = string.Empty;

    public HostDashboardOccupancyDto Occupancy { get; init; } = new();
}

/// <summary>Bookings by channel: the confirmed stays of the Home cut by where they came from.</summary>
public sealed class BookingsByChannelReportDto
{
    public string Title { get; init; } = string.Empty;

    /// <summary>Always <c>accrual</c>: the confirmed stays with a night in the period, their revenue by nights as the Home.</summary>
    public string Basis { get; init; } = "accrual";

    public string Currency { get; init; } = "EUR";

    public ReportPeriodDto Period { get; init; } = new();

    /// <summary>The channels with at least one stay in the period, the one with most stays first.</summary>
    public IReadOnlyList<BookingsByChannelRowDto> Rows { get; init; } = [];

    public BookingsByChannelTotalDto Total { get; init; } = new();

    public static BookingsByChannelReportDto From(HostBookingsByChannel report, string title) => new()
    {
        Title = title,
        Period = ReportPeriodDto.Of(report.Period),
        Rows = report.Rows.Select(r => BookingsByChannelRowDto.From(r, report.BookingCount)).ToList(),
        Total = new BookingsByChannelTotalDto
        {
            BookingCount = report.BookingCount,
            Nights = report.Nights,
            Amount = report.Revenue,
            AmountCents = PaymentCashRules.ToCents(report.Revenue),
        },
    };
}

public sealed class BookingsByChannelRowDto
{
    /// <summary>The channel, the name of the booking source: <c>Direct</c>, <c>Manual</c>, <c>Airbnb</c>, <c>BookingCom</c>, ... A key, never text to show.</summary>
    public string Source { get; init; } = string.Empty;

    /// <summary>Confirmed stays with at least one night in the period.</summary>
    public int BookingCount { get; init; }

    /// <summary>Their nights that fall in the period.</summary>
    public int Nights { get; init; }

    /// <summary>Their revenue in euros, rounded to the cent.</summary>
    public decimal Amount { get; init; }

    /// <summary><see cref="Amount"/> in cents of euro, exact.</summary>
    public long AmountCents { get; init; }

    /// <summary>The part of all the stays this channel has, from 0 to 1.</summary>
    public decimal Share { get; init; }

    public static BookingsByChannelRowDto From(HostBookingsByChannelRow row, int total) => new()
    {
        Source = row.Source.ToString(),
        BookingCount = row.BookingCount,
        Nights = row.Nights,
        Amount = row.Revenue,
        AmountCents = PaymentCashRules.ToCents(row.Revenue),
        Share = total == 0 ? 0m : (decimal)row.BookingCount / total,
    };
}

/// <summary>The stays, nights and revenue of every channel together: the Home's for the same days.</summary>
public sealed class BookingsByChannelTotalDto
{
    public int BookingCount { get; init; }

    public int Nights { get; init; }

    /// <summary>Euros, rounded to the cent.</summary>
    public decimal Amount { get; init; }

    /// <summary><see cref="Amount"/> in cents of euro, exact.</summary>
    public long AmountCents { get; init; }
}
