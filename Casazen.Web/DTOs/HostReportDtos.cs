using Casazen.Core.Services;

namespace Casazen.Web.DTOs;

public sealed class ReportPeriodDto
{
    public DateOnly From { get; init; }

    public DateOnly To { get; init; }

    public int Nights { get; init; }

    public static ReportPeriodDto Of(HostDashboardPeriod period) => new()
    {
        From = DateOnly.FromDateTime(period.From),
        To = DateOnly.FromDateTime(period.To.AddDays(-1)),
        Nights = period.Nights,
    };
}

public sealed class RevenueByPropertyReportDto
{
    public string Title { get; init; } = string.Empty;

    public string Basis { get; init; } = "accrual";

    public string Currency { get; init; } = "EUR";

    public ReportPeriodDto Period { get; init; } = new();

    public IReadOnlyList<RevenueByPropertyRowDto> Rows { get; init; } = [];

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

    public decimal Amount { get; init; }

    public long AmountCents { get; init; }

    public int StayCount { get; init; }

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

public sealed class ReportRevenueTotalDto
{
    public decimal Amount { get; init; }

    public long AmountCents { get; init; }

    public int StayCount { get; init; }

    public int Nights { get; init; }
}

public sealed class OccupancyByPropertyReportDto
{
    public string Title { get; init; } = string.Empty;

    public string Basis { get; init; } = "nights";

    public ReportPeriodDto Period { get; init; } = new();

    public IReadOnlyList<OccupancyByPropertyRowDto> Rows { get; init; } = [];

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

public sealed class BookingsByChannelReportDto
{
    public string Title { get; init; } = string.Empty;

    public string Basis { get; init; } = "accrual";

    public string Currency { get; init; } = "EUR";

    public ReportPeriodDto Period { get; init; } = new();

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

    public int BookingCount { get; init; }

    public int Nights { get; init; }

    public decimal Amount { get; init; }

    public long AmountCents { get; init; }

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

public sealed class BookingsByChannelTotalDto
{
    public int BookingCount { get; init; }

    public int Nights { get; init; }

    public decimal Amount { get; init; }

    public long AmountCents { get; init; }
}
