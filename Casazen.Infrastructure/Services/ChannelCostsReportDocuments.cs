using System.Globalization;
using Casazen.Core.Services;
using Casazen.Core.Utilities;

namespace Casazen.Infrastructure.Services;

public static class ChannelCostsReportDocuments
{
    public const string Title = "Costi dei canali, per competenza";

    public const string SheetName = "Costi dei canali";

    private const string Total = "Totale";

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

    public static string FileName(HostDashboardPeriod period, string extension = "csv") =>
        HostReportDocuments.FileName("costi-dei-canali-competenza", period, extension);

    public static string? KindLabel(ChannelCostKind? kind) => kind switch
    {
        ChannelCostKind.ChannelCommission => "Commissione del canale",
        ChannelCostKind.PaymentFee => "Commissioni sui pagamenti con carta",
        ChannelCostKind.NoCost => "Nessun costo",
        _ => null,
    };

    public static string? ModeLabel(ChannelCommissionMode? mode) => mode switch
    {
        ChannelCommissionMode.DeductedFromPayout => "Trattenuta dal pagamento",
        ChannelCommissionMode.InvoicedSeparately => "Fatturata a parte",
        ChannelCommissionMode.Unknown => "Non nota",
        _ => null,
    };

    private static string Date(DateOnly day) => day.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture);
}
