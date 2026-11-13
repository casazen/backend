using System.Globalization;
using Casazen.Core.Services;
using Casazen.Core.Utilities;

namespace Casazen.Infrastructure.Services;

/// <summary>
/// The title and the CSV file of the channel costs report (NF-98), in Italian like the other host reports (<see cref="HostReportDocuments"/>): for a spreadsheet set
/// to Italian (<see cref="CsvWriter"/>). The file carries the title with its base and the days on its first row, so no figure travels without its label, and a cost
/// nobody reported is an empty cell, never a zero. Nothing about a guest: channels, counts and amounts only.
/// </summary>
public static class ChannelCostsReportDocuments
{
    /// <summary>Title of the channel costs: by accrual, the nights of the confirmed stays, as the revenue of the Home.</summary>
    public const string Title = "Costi dei canali, per competenza";

    private const string Total = "Totale";

    /// <summary>
    /// The report as a file: the title with the days on the first row, the header, a row for each channel (the most costly first) and the total. The cost, the net and
    /// the rate are empty where nobody reported a cost; the stays without one are in their own column.
    /// </summary>
    public static byte[] Csv(ChannelCostsReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        var rows = new List<IReadOnlyList<object?>>
        {
            Row($"{Title}, dal {Date(DateOnly.FromDateTime(report.Period.From))} al {Date(DateOnly.FromDateTime(report.Period.To.AddDays(-1)))}"),
            Row(
                "Canale",
                "Tipo di costo",
                "Soggiorni",
                "Lordo per competenza (€)",
                "Costo (€)",
                "Netto (€)",
                "Costo sul lordo (%)",
                "Come si paga la commissione",
                "Soggiorni senza costo indicato",
                "Il più costoso"),
        };
        rows.AddRange(report.Rows.Select(r => Row(
            r.Source is { } source ? FiscalReportDocuments.ChannelLabel(source.ToString()) : Total,
            KindLabel(r.Kind),
            r.StayCount,
            r.Gross,
            r.Commission,
            r.Net,
            Percent(r.CostRate),
            ModeLabel(r.CommissionMode),
            r.StaysWithoutCommission,
            r.MostExpensive ? "Sì" : null)));
        var t = report.Total;
        rows.Add(Row(Total, null, t.StayCount, t.Gross, t.Commission, t.Net, Percent(t.CostRate), null, t.StaysWithoutCommission, null));
        return CsvWriter.Write(rows);
    }

    /// <summary>The file name: <c>casazen-costi-dei-canali-competenza-20261001-20261031.csv</c>.</summary>
    public static string FileName(HostDashboardPeriod period) => HostReportDocuments.FileName("costi-dei-canali-competenza", period);

    /// <summary>What a kind of cost is, in the file.</summary>
    public static string? KindLabel(ChannelCostKind? kind) => kind switch
    {
        ChannelCostKind.ChannelCommission => "Commissione del canale",
        ChannelCostKind.PaymentFee => "Commissioni sui pagamenti con carta",
        ChannelCostKind.NoCost => "Nessun costo",
        _ => null,
    };

    /// <summary>How a commission is settled, in the file.</summary>
    public static string? ModeLabel(ChannelCommissionMode? mode) => mode switch
    {
        ChannelCommissionMode.DeductedFromPayout => "Trattenuta dal pagamento",
        ChannelCommissionMode.InvoicedSeparately => "Fatturata a parte",
        ChannelCommissionMode.Unknown => "Non nota",
        _ => null,
    };

    // A rate (0 to 1) as a percentage to the cent of a point; empty without one.
    private static decimal? Percent(decimal? rate) => rate is { } value ? Math.Round(value * 100m, 2, MidpointRounding.AwayFromZero) : null;

    private static IReadOnlyList<object?> Row(params object?[] cells) => cells;

    private static string Date(DateOnly day) => day.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture);
}
