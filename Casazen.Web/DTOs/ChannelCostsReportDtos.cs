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
    /// <summary>The name of the report with its base (Italian, the one of the file).</summary>
    public string Title { get; init; } = string.Empty;

    /// <summary>Always <c>accrual</c>: the nights of the confirmed stays, whenever the money came in.</summary>
    public string Basis { get; init; } = "accrual";

    public ReportPeriodDto Period { get; init; } = new();

    /// <summary>The channels with a stay in the period, by cost rate, the highest first (the ones without a rate last).</summary>
    public IReadOnlyList<ChannelCostRowDto> Rows { get; init; } = [];

    /// <summary>Every channel together: the Home's revenue of the same days as its gross and the costs that are known as its commission (its <c>source</c>, <c>kind</c> and <c>commissionMode</c> are null).</summary>
    public ChannelCostRowDto Total { get; init; } = new();

    public static ChannelCostsReportDto From(ChannelCostsReport report, string title) => new()
    {
        Title = title,
        Period = ReportPeriodDto.Of(report.Period),
        Rows = report.Rows.Select(ChannelCostRowDto.From).ToList(),
        Total = ChannelCostRowDto.From(report.Total),
    };
}

/// <summary>What one channel costs.</summary>
public sealed class ChannelCostRowDto
{
    /// <summary>The channel, the name of the booking source: <c>Direct</c>, <c>Manual</c>, <c>Airbnb</c>, <c>BookingCom</c>, ... A key, never text to show; null for the total.</summary>
    public string? Source { get; init; }

    /// <summary>
    /// What the cost is: <c>ChannelCommission</c> (the commission of the channel), <c>PaymentFee</c> (the card provider's fees of the direct stays: shown as their own
    /// line) or <c>NoCost</c> (nobody charges the host). A key, never text to show; null for the total.
    /// </summary>
    public string? Kind { get; init; }

    /// <summary>The confirmed stays with a night in the period.</summary>
    public int StayCount { get; init; }

    /// <summary>The Home's revenue of those stays, pro rata per night.</summary>
    public decimal Gross { get; init; }

    public long GrossCents { get; init; }

    /// <summary>What the stays that report a cost cost, pro rata; null (not available) when none of them does.</summary>
    public decimal? Commission { get; init; }

    public long? CommissionCents { get; init; }

    /// <summary>What is left of the stays whose cost is known (the channel's reported net, else gross minus cost); null when no stay has either.</summary>
    public decimal? Net { get; init; }

    public long? NetCents { get; init; }

    /// <summary>The cost over the gross of the stays that report it, from 0 to 1; null when no stay reports a cost. The client writes "how much each channel costs" from it and from <see cref="Commission"/>.</summary>
    public decimal? CostRate { get; init; }

    /// <summary>
    /// How the commission is settled: <c>DeductedFromPayout</c> (the channel pays the host net), <c>InvoicedSeparately</c> (the host pays it afterwards) or
    /// <c>Unknown</c>; null where it does not apply (not a channel, and the total). A key, never text to show.
    /// </summary>
    public string? CommissionMode { get; init; }

    /// <summary>The stays whose cost was not reported: counted apart, never taken as free, and not in the commission, the net or the rate.</summary>
    public int StaysWithoutCommission { get; init; }

    /// <summary>True for the row with the highest cost rate (at most one, never a rate of zero).</summary>
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
