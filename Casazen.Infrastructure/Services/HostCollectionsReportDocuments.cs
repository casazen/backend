using System.Globalization;
using Casazen.Core.Entities;
using Casazen.Core.Services;
using Casazen.Core.Utilities;

namespace Casazen.Infrastructure.Services;

public static class HostCollectionsReportDocuments
{
    public const string CollectionsTitle = "Incassato, per cassa";

    public const string BalancesTitle = "Saldi da incassare";

    private const string Total = "Totale";

    public static IReadOnlyList<ReportTable> Tables(HostCollectionsReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        var total = report.Total;
        var byDate = new ReportTable(Title("Incassato per data", report.Period), ["Dal", "Al", "Incassato (€)", "Pagamenti"]);
        foreach (var b in report.ByDate)
            byDate.Add(ReportCell.Date(b.From), ReportCell.Date(b.To), ReportCell.Money(b.Amount), b.PaymentCount);
        byDate.Add(Total, ReportCell.Empty, ReportCell.Money(total.Amount), total.PaymentCount);

        var byMethod = new ReportTable(
            Title("Incassato per metodo di pagamento", report.Period), ["Metodo di pagamento", "Incassato (€)", "Pagamenti", "Quota (%)"]);
        foreach (var m in report.ByMethod)
            byMethod.Add(MethodLabel(m.Method), ReportCell.Money(m.Amount), m.PaymentCount, ReportCell.Percent(m.Share));
        byMethod.Add(Total, ReportCell.Money(total.Amount), total.PaymentCount, ReportCell.Percent(total.Amount > 0m ? 1m : null));

        var byChannel = new ReportTable(Title("Incassato per canale", report.Period), ["Canale", "Incassato (€)", "Pagamenti", "Quota (%)"]);
        foreach (var c in report.ByChannel)
        {
            byChannel.Add(
                FiscalReportDocuments.ChannelLabel(c.Source.ToString()), ReportCell.Money(c.Amount), c.PaymentCount, ReportCell.Percent(c.Share));
        }

        byChannel.Add(Total, ReportCell.Money(total.Amount), total.PaymentCount, ReportCell.Percent(total.Amount > 0m ? 1m : null));
        return [byDate, byMethod, byChannel];
    }

    public static byte[] Csv(HostCollectionsReport report) => CsvWriter.Write(Tables(report));

    public static byte[] Xlsx(HostCollectionsReport report)
    {
        var tables = Tables(report);
        return XlsxWriter.Write(
        [
            new ReportSheet("Incassato per data", tables[0]),
            new ReportSheet("Incassato per metodo", tables[1]),
            new ReportSheet("Incassato per canale", tables[2]),
        ]);
    }

    public static ReportTable Table(HostBalancesToCollect report)
    {
        ArgumentNullException.ThrowIfNull(report);

        var table = new ReportTable(
            $"{BalancesTitle}, al {Date(report.AsOf)}",
            [
                "Codice prenotazione",
                "Immobile",
                "Canale",
                "Check-in",
                "Check-out",
                "Valuta",
                "Totale",
                "Incassato",
                "Da incassare",
                "Scaduto",
                "Importi indicati da",
            ]);
        foreach (var r in report.Rows)
        {
            table.Add(
                BookingCodes.Format(r.BookingCode),
                r.PropertyName,
                FiscalReportDocuments.ChannelLabel(r.Source.ToString()),
                ReportCell.Date(r.CheckIn),
                ReportCell.Date(r.CheckOut),
                r.Currency,
                ReportCell.Money(r.Total),
                ReportCell.Money(r.Collected),
                ReportCell.Money(r.ToCollect),
                r.Overdue ? "Sì" : "No",
                r.Basis == HostBalanceBasis.ChannelManager ? "Channel manager" : "CasaZen");
        }

        foreach (var t in report.Totals)
        {
            table.Add(
                Total,
                ReportCell.Empty,
                ReportCell.Empty,
                ReportCell.Empty,
                ReportCell.Empty,
                t.Currency,
                ReportCell.Empty,
                ReportCell.Empty,
                ReportCell.Money(t.Amount),
                ReportCell.Empty,
                ReportCell.Empty);
        }

        return table;
    }

    public static byte[] Csv(HostBalancesToCollect report) => CsvWriter.Write(Table(report));

    public static byte[] Xlsx(HostBalancesToCollect report) => XlsxWriter.Write([new ReportSheet("Saldi da incassare", Table(report))]);

    public static string BalancesFileName(DateOnly asOf, string extension = "csv")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(extension);

        return $"casazen-saldi-da-incassare-{asOf.ToString("yyyyMMdd", CultureInfo.InvariantCulture)}.{extension}";
    }

    public static string MethodLabel(PaymentMethod method) => method switch
    {
        PaymentMethod.CreditCard => "Carta",
        PaymentMethod.BankTransfer => "Bonifico",
        PaymentMethod.PayPal => "PayPal",
        PaymentMethod.ApplePay => "Apple Pay",
        PaymentMethod.GooglePay => "Google Pay",
        PaymentMethod.CashOnArrival => "Contanti all'arrivo",
        _ => method.ToString(),
    };

    private static string Title(string title, HostDashboardPeriod period) =>
        $"{title}, per cassa, dal {Date(DateOnly.FromDateTime(period.From))} al {Date(DateOnly.FromDateTime(period.To.AddDays(-1)))}";

    private static string Date(DateOnly day) => day.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture);
}
