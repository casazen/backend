using System.Globalization;
using Casazen.Core.Services;
using Casazen.Core.Utilities;

namespace Casazen.Infrastructure.Services;

/// <summary>
/// The title and the CSV file of the tourist tax summary (NF-100), in Italian like the fiscal reports (<see cref="FiscalReportDocuments"/>)
/// and the reports of the host (<see cref="HostReportDocuments"/>): for a spreadsheet set to Italian (<see cref="CsvWriter"/>). The title says
/// which base the numbers are on, and the file carries it on its first row with the days, so no figure travels without its label. Nothing
/// about a guest: comuni, counts, amounts and the payments of the host.
/// </summary>
public static class TouristTaxSummaryDocuments
{
    /// <summary>
    /// The name of the summary with its base: the tax owed is the one recorded on the stays that check in during the period, the tax paid is
    /// that of the payments whose period lies inside it.
    /// </summary>
    public const string SummaryTitle =
        "Tassa di soggiorno per comune: dovuta sui soggiorni con check-in nel periodo, versata con periodo compreso nel periodo";

    private const string Total = "Totale";

    /// <summary>
    /// The summary as a file: the title with its base and the days on the first row, the header, a row for each comune with what is owed,
    /// what was paid and the balance, the <c>Totale</c> row; then, after an empty row, the payments (a row for each, with its comune).
    /// </summary>
    public static byte[] Csv(TouristTaxSummary summary)
    {
        ArgumentNullException.ThrowIfNull(summary);

        var rows = new List<IReadOnlyList<object?>>
        {
            Row($"{SummaryTitle}, dal {Date(summary.Period.From)} al {Date(summary.Period.To)}"),
            Row(
                "Comune",
                "Soggiorni",
                "Notti",
                "Ospiti",
                "Imposta dovuta (€)",
                "Soggiorni senza importo",
                "Imposta versata (€)",
                "Saldo (€)",
                "Stato"),
        };
        rows.AddRange(summary.Comuni.Select(c => Row(
            c.Comune, c.Stays, c.Nights, c.Guests, c.Owed, c.StaysWithoutAmount, c.Paid, c.Balance, StatusLabel(c.Status))));
        var t = summary.Total;
        rows.Add(Row(Total, t.Stays, t.Nights, t.Guests, t.Owed, t.StaysWithoutAmount, t.Paid, t.Balance, null));

        rows.Add(Row());
        rows.Add(Row("Versamenti registrati"));
        rows.Add(Row("Comune", "Periodo dal", "Periodo al", "Importo versato (€)", "Data del versamento", "Riferimento", "Note", "Ricevuta"));
        foreach (var comune in summary.Comuni)
        {
            rows.AddRange(comune.Remittances.Select(r => Row(
                comune.Comune,
                Date(r.PeriodFrom),
                Date(r.PeriodTo),
                r.Amount,
                Date(r.PaidOn),
                r.Reference,
                r.Notes,
                r.HasReceipt ? "Sì" : "No")));
        }

        return CsvWriter.Write(rows);
    }

    /// <summary>The file name of the summary: <c>casazen-tassa-soggiorno-riepilogo-20260101-20261231.csv</c>.</summary>
    public static string FileName(FiscalReportPeriod period)
    {
        ArgumentNullException.ThrowIfNull(period);

        return $"casazen-tassa-soggiorno-riepilogo-{Compact(period.From)}-{Compact(period.To)}.csv";
    }

    /// <summary>The status in Italian, as the file says it.</summary>
    public static string StatusLabel(TouristTaxSummaryStatus status) => status switch
    {
        TouristTaxSummaryStatus.NothingOwed => "Niente da versare",
        TouristTaxSummaryStatus.ToPay => "Da versare",
        TouristTaxSummaryStatus.Paid => "Versata",
        TouristTaxSummaryStatus.Overpaid => "Versata in più",
        _ => status.ToString(),
    };

    private static IReadOnlyList<object?> Row(params object?[] cells) => cells;

    private static string Date(DateOnly day) => day.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture);

    private static string Compact(DateOnly day) => day.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
}
