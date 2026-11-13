using System.Globalization;
using Casazen.Core.Entities;
using Casazen.Core.Services;
using Casazen.Core.Utilities;

namespace Casazen.Infrastructure.Services;

/// <summary>
/// The titles and the CSV files of the collections report (NF-97), in Italian like the other host reports (<see cref="HostReportDocuments"/>): for a
/// spreadsheet set to Italian (<see cref="CsvWriter"/>). Every table carries a title row that says what its numbers are (by cash, net of refunds) and the
/// days, so no figure travels without its label. Nothing about a guest: dates, properties, channels, counts and amounts only.
/// </summary>
public static class HostCollectionsReportDocuments
{
    /// <summary>Title of the money collected: by cash, net of what was refunded, as the Home's "incassato".</summary>
    public const string CollectionsTitle = "Incassato, per cassa";

    /// <summary>Title of the balances still to collect.</summary>
    public const string BalancesTitle = "Saldi da incassare";

    private const string Total = "Totale";

    // ---------------------------------------------------------------- files

    /// <summary>
    /// The collections as a file: the three cuts one under the other, each under its own title row with the days of the report, its header and its total, and
    /// an empty row between them. The credits are not in the file: they are not a table of the same money.
    /// </summary>
    public static byte[] Csv(HostCollectionsReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        var total = report.Total;
        var rows = new List<IReadOnlyList<object?>>
        {
            Title("Incassato per data", report.Period),
            Row("Dal", "Al", "Incassato (€)", "Pagamenti"),
        };
        rows.AddRange(report.ByDate.Select(b => Row(Date(b.From), Date(b.To), b.Amount, b.PaymentCount)));
        rows.Add(Row(Total, null, total.Amount, total.PaymentCount));

        rows.Add(Row());
        rows.Add(Title("Incassato per metodo di pagamento", report.Period));
        rows.Add(Row("Metodo di pagamento", "Incassato (€)", "Pagamenti", "Quota (%)"));
        rows.AddRange(report.ByMethod.Select(m => Row(MethodLabel(m.Method), m.Amount, m.PaymentCount, Percent(m.Share))));
        rows.Add(Row(Total, total.Amount, total.PaymentCount, Percent(total.Amount > 0m ? 1m : null)));

        rows.Add(Row());
        rows.Add(Title("Incassato per canale", report.Period));
        rows.Add(Row("Canale", "Incassato (€)", "Pagamenti", "Quota (%)"));
        rows.AddRange(report.ByChannel.Select(c => Row(
            FiscalReportDocuments.ChannelLabel(c.Source.ToString()), c.Amount, c.PaymentCount, Percent(c.Share))));
        rows.Add(Row(Total, total.Amount, total.PaymentCount, Percent(total.Amount > 0m ? 1m : null)));
        return CsvWriter.Write(rows);
    }

    /// <summary>
    /// The balances to collect as a file: the title with the day they are read on, the header, a row for each stay and a total for each currency. A figure the
    /// channel manager did not give is an empty cell, never a zero.
    /// </summary>
    public static byte[] Csv(HostBalancesToCollect report)
    {
        ArgumentNullException.ThrowIfNull(report);

        var rows = new List<IReadOnlyList<object?>>
        {
            Row($"{BalancesTitle}, al {Date(report.AsOf)}"),
            Row(
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
                "Importi indicati da"),
        };
        rows.AddRange(report.Rows.Select(r => Row(
            BookingCodes.Format(r.BookingCode),
            r.PropertyName,
            FiscalReportDocuments.ChannelLabel(r.Source.ToString()),
            Date(r.CheckIn),
            Date(r.CheckOut),
            r.Currency,
            r.Total,
            r.Collected,
            r.ToCollect,
            r.Overdue ? "Sì" : "No",
            r.Basis == HostBalanceBasis.ChannelManager ? "Channel manager" : "CasaZen")));
        rows.AddRange(report.Totals.Select(t => Row(Total, null, null, null, null, t.Currency, null, null, t.Amount, null, null)));
        return CsvWriter.Write(rows);
    }

    /// <summary>The file name of the balances: <c>casazen-saldi-da-incassare-20261112.csv</c>, with the day they are read on.</summary>
    public static string BalancesFileName(DateOnly asOf) =>
        $"casazen-saldi-da-incassare-{asOf.ToString("yyyyMMdd", CultureInfo.InvariantCulture)}.csv";

    /// <summary>How a payment method is written in the file.</summary>
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

    // ---------------------------------------------------------------- shared

    private static IReadOnlyList<object?> Title(string title, HostDashboardPeriod period) =>
        Row($"{title}, per cassa, dal {Date(DateOnly.FromDateTime(period.From))} al {Date(DateOnly.FromDateTime(period.To.AddDays(-1)))}");

    // A share (0 to 1) as a percentage to the cent of a point; empty without one.
    private static decimal? Percent(decimal? share) =>
        share is { } value ? Math.Round(value * 100m, 2, MidpointRounding.AwayFromZero) : null;

    private static IReadOnlyList<object?> Row(params object?[] cells) => cells;

    private static string Date(DateOnly day) => day.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture);
}
