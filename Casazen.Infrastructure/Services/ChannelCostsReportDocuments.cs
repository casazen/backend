using System.Globalization;
using Casazen.Core.Services;
using Casazen.Core.Utilities;

namespace Casazen.Infrastructure.Services;

/// <summary>
/// The title, the CSV file and the Excel file of the channel costs report (NF-98, NF-101), in Italian like the other host reports (<see cref="HostReportDocuments"/>): the CSV
/// for a spreadsheet set to Italian (<see cref="CsvWriter"/>). The file carries the title with its base and the days on its first row, so no figure travels without its
/// label, and a cost nobody reported is an empty cell, never a zero. Nothing about a guest: channels, counts and amounts only.
/// </summary>
public static class ChannelCostsReportDocuments
{
    /// <summary>Title of the channel costs: by accrual, the nights of the confirmed stays, as the revenue of the Home.</summary>
    public const string Title = "Costi dei canali, per competenza";

    /// <summary>Name of the sheet of the Excel file.</summary>
    public const string SheetName = "Costi dei canali";

    private const string Total = "Totale";

    /// <summary>
    /// The report as a table: the title with the days on the first row, the header, a row for each channel (the most costly first) and the total. The cost, the net and
    /// the rate are empty where nobody reported a cost; the stays without one are in their own column.
    /// </summary>
    public static ReportTable Table(ChannelCostsReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        var table = new ReportTable(
            $"{Title}, dal {Date(DateOnly.FromDateTime(report.Period.From))} al {Date(DateOnly.FromDateTime(report.Period.To.AddDays(-1)))}",
            [
                "Canale",
                "Tipo di costo",
                "Soggiorni",
                "Lordo per competenza (€)",
                "Costo (€)",
                "Netto (€)",
                "Costo sul lordo (%)",
                "Come si paga la commissione",
                "Soggiorni senza costo indicato",
                "Il più costoso",
            ]);
        foreach (var r in report.Rows)
        {
            table.Add(
                r.Source is { } source ? FiscalReportDocuments.ChannelLabel(source.ToString()) : Total,
                KindLabel(r.Kind),
                r.StayCount,
                ReportCell.Money(r.Gross),
                ReportCell.Money(r.Commission),
                ReportCell.Money(r.Net),
                ReportCell.Percent(r.CostRate),
                ModeLabel(r.CommissionMode),
                r.StaysWithoutCommission,
                r.MostExpensive ? "Sì" : null);
        }

        var t = report.Total;
        return table.Add(
            Total,
            ReportCell.Empty,
            t.StayCount,
            ReportCell.Money(t.Gross),
            ReportCell.Money(t.Commission),
            ReportCell.Money(t.Net),
            ReportCell.Percent(t.CostRate),
            ReportCell.Empty,
            t.StaysWithoutCommission,
            ReportCell.Empty);
    }

    public static byte[] Csv(ChannelCostsReport report) => CsvWriter.Write(Table(report));

    public static byte[] Xlsx(ChannelCostsReport report) => XlsxWriter.Write([new ReportSheet(SheetName, Table(report))]);

    /// <summary>The file name: <c>casazen-costi-dei-canali-competenza-20261001-20261031.csv</c>; <paramref name="extension"/> is <c>csv</c> or <c>xlsx</c>.</summary>
    public static string FileName(HostDashboardPeriod period, string extension = "csv") =>
        HostReportDocuments.FileName("costi-dei-canali-competenza", period, extension);

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

    private static string Date(DateOnly day) => day.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture);
}
