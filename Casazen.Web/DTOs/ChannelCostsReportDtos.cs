using Casazen.Core.Services;

namespace Casazen.Web.DTOs;

/// <summary>
/// The channel costs report of the host (NF-98, <c>GET /api/reports/channel-costs</c>): what each channel costs, by accrual like the revenue of the Home. The
/// gross is the Home's revenue of the confirmed stays with a night in the period; the commission and the net are what the channel manager stored on the imported
/// bookings, pro rata per night; for the direct stays the cost is the fees of their card payments. <b>A cost nobody reported is null, never zero</b>: the page says
/// "not available", and the stays without one are counted apart in <c>staysWithoutCommission</c>. Amounts are in euros to the cent and also in cents (exact).
/// Definitions in <see cref="IChannelCostsReportService"/> and <c>docs/TECHNICAL.md</c> ("Money reports").
/// </summary>
public sealed class ChannelCostsReportDto
{
    public string Title { get; init; } = string.Empty;

    public string Basis { get; init; } = "accrual";

    public ReportPeriodDto Period { get; init; } = new();

    public IReadOnlyList<ChannelCostRowDto> Rows { get; init; } = [];

    public ChannelCostRowDto Total { get; init; } = new();

    public static ChannelCostsReportDto From(ChannelCostsReport report, string title) => new()
    {
        Title = title,
        Period = ReportPeriodDto.Of(report.Period),
        Rows = report.Rows.Select(ChannelCostRowDto.From).ToList(),
        Total = ChannelCostRowDto.From(report.Total),
    };
}

public sealed class ChannelCostRowDto
{
    /// <summary>The channel, the name of the booking source: <c>Direct</c>, <c>Manual</c>, <c>Airbnb</c>, <c>BookingCom</c>, ... A key, never text to show; null for the total.</summary>
    public string? Source { get; init; }

    public string? Kind { get; init; }

    public int StayCount { get; init; }

    public decimal Gross { get; init; }

    public long GrossCents { get; init; }

    public decimal? Commission { get; init; }

    public long? CommissionCents { get; init; }

    public decimal? Net { get; init; }

    public long? NetCents { get; init; }

    public decimal? CostRate { get; init; }

    public string? CommissionMode { get; init; }

    public int StaysWithoutCommission { get; init; }

    public bool MostExpensive { get; init; }

    public static ChannelCostRowDto From(ChannelCostRow row) => new()
    {
        Source = row.Source?.ToString(),
        Kind = row.Kind?.ToString(),
        StayCount = row.StayCount,
        Gross = row.Gross,
        GrossCents = PaymentCashRules.ToCents(row.Gross),
        Commission = row.Commission,
        CommissionCents = row.Commission is { } commission ? PaymentCashRules.ToCents(commission) : null,
        Net = row.Net,
        NetCents = row.Net is { } net ? PaymentCashRules.ToCents(net) : null,
        CostRate = row.CostRate,
        CommissionMode = row.CommissionMode?.ToString(),
        StaysWithoutCommission = row.StaysWithoutCommission,
        MostExpensive = row.MostExpensive,
    };
}
