using System.Globalization;
using Casazen.Core.Services;
using Casazen.Core.Utilities;

namespace Casazen.Infrastructure.Services;

/// <summary>
/// The two words of the CSV that come from the resources (<c>SharedResources</c>, Italian and English). The web layer reads them in Italian:
/// the file is Italian (title, header, statuses) whatever the language of the caller, so a file for the accountant never mixes two languages.
/// </summary>
/// <param name="NothingRecorded">The status of a comune with nothing recorded on its stays and nothing paid. It says that no amount is
/// recorded, never that nothing is due: a stay with no amount is not a stay with a tax of zero.</param>
/// <param name="IncompleteAmount">The note of a row (and of the total) with stays that have no amount recorded: the owed amount may be incomplete.</param>
public sealed record TouristTaxCsvTexts(string NothingRecorded, string IncompleteAmount);

/// <summary>
/// The title, the CSV file and the Excel file of the tourist tax summary (NF-100, NF-101), in Italian like the fiscal reports (<see cref="FiscalReportDocuments"/>)
/// and the reports of the host (<see cref="HostReportDocuments"/>): the CSV for a spreadsheet set to Italian (<see cref="CsvWriter"/>). The title says
/// which base the numbers are on, and the file carries it on its first row with the days, so no figure travels without its label. Nothing
/// about a guest: comuni, counts, amounts and the payments of the host. The CSV has the payments under the comuni; the Excel file has a sheet for each, with the same
/// columns and the same numbers.
/// </summary>
public static class TouristTaxSummaryDocuments
{
    /// <summary>
    /// The name of the summary with its base: the tax owed is the one recorded on the stays that check in during the period, the tax paid is
    /// that of the payments whose period lies inside it.
    /// </summary>
    public const string SummaryTitle =
        "Tassa di soggiorno per comune: dovuta sui soggiorni con check-in nel periodo, versata con periodo compreso nel periodo";

    /// <summary>
    /// The name of the summary of a caller who reaches only some of the properties: the tax owed on the stays of those properties and nothing
    /// about the payments, which belong to the org and not to a property (<see cref="TouristTaxSummary.PaymentsHidden"/>).
    /// </summary>
    public const string OwedOnlyTitle =
        "Tassa di soggiorno per comune: dovuta sui soggiorni con check-in nel periodo, solo degli immobili dell'ambito di accesso (versamenti non mostrati)";

    /// <summary>Title of the table of the payments the host recorded.</summary>
    public const string RemittancesTitle = "Versamenti registrati";

    /// <summary>Name of the sheet of the comuni.</summary>
    public const string ComuniSheet = "Per comune";

    /// <summary>Name of the sheet of the payments.</summary>
    public const string RemittancesSheet = "Versamenti registrati";

    private const string Total = "Totale";

    /// <summary>The name of <paramref name="summary"/> with its base: with the payments, or without them when they are hidden.</summary>
    public static string TitleOf(TouristTaxSummary summary)
    {
        ArgumentNullException.ThrowIfNull(summary);

        return summary.PaymentsHidden ? OwedOnlyTitle : SummaryTitle;
    }

    /// <summary>
    /// The summary as tables: the title with its base and the days on the first row, the header, a row for each comune with what is owed,
    /// what was paid and the balance, the <c>Totale</c> row; then the payments (a row for each, with its comune).
    /// The last column is the note: «Importo incompleto» on every row (and on the total) that counts stays with no amount recorded, because
    /// then the owed amount, and so the balance and the status, may be incomplete. When the payments are hidden (a caller who reaches only
    /// some of the properties) there is the owed side only: no paid, balance and status columns and no table of payments.
    /// </summary>
    public static IReadOnlyList<ReportTable> Tables(TouristTaxSummary summary, TouristTaxCsvTexts texts)
    {
        ArgumentNullException.ThrowIfNull(summary);
        ArgumentNullException.ThrowIfNull(texts);

        var hidden = summary.PaymentsHidden;
        var header = new List<string> { "Comune", "Soggiorni", "Notti", "Ospiti", "Imposta dovuta (€)", "Soggiorni senza importo" };
        if (!hidden)
            header.AddRange(["Imposta versata (€)", "Saldo (€)", "Stato"]);
        header.Add("Nota");

        var comuni = new ReportTable($"{TitleOf(summary)}, dal {Date(summary.Period.From)} al {Date(summary.Period.To)}", header);
        foreach (var comune in summary.Comuni)
            comuni.Add(ComuneRow(comune, hidden, texts));

        comuni.Add(TotalRow(summary.Total, hidden, texts));

        // The payments belong to the org: a summary that hides them has no list of them either.
        if (hidden)
            return [comuni];

        var remittances = new ReportTable(
            RemittancesTitle,
            ["Comune", "Periodo dal", "Periodo al", "Importo versato (€)", "Data del versamento", "Riferimento", "Note", "Ricevuta"]);
        foreach (var comune in summary.Comuni)
        {
            foreach (var r in comune.Remittances ?? [])
            {
                remittances.Add(
                    comune.Comune,
                    ReportCell.Date(r.PeriodFrom),
                    ReportCell.Date(r.PeriodTo),
                    ReportCell.Money(r.Amount),
                    ReportCell.Date(r.PaidOn),
                    r.Reference,
                    r.Notes,
                    r.HasReceipt ? "Sì" : "No");
            }
        }

        return [comuni, remittances];
    }

    /// <summary>The CSV of the summary: the comuni, then, after an empty row, the payments (nothing but the comuni when the payments are hidden).</summary>
    public static byte[] Csv(TouristTaxSummary summary, TouristTaxCsvTexts texts) => CsvWriter.Write(Tables(summary, texts));

    /// <summary>The Excel file of the summary: a sheet for the comuni and one for the payments (the comuni alone when the payments are hidden).</summary>
    public static byte[] Xlsx(TouristTaxSummary summary, TouristTaxCsvTexts texts)
    {
        var tables = Tables(summary, texts);
        var sheets = new List<ReportSheet> { new(ComuniSheet, tables[0]) };
        if (tables.Count > 1)
            sheets.Add(new ReportSheet(RemittancesSheet, tables[1]));

        return XlsxWriter.Write(sheets);
    }

    /// <summary>The file name of the summary: <c>casazen-tassa-soggiorno-riepilogo-20260101-20261231.csv</c>; <paramref name="extension"/> is <c>csv</c> or <c>xlsx</c>.</summary>
    public static string FileName(FiscalReportPeriod period, string extension = "csv")
    {
        ArgumentNullException.ThrowIfNull(period);
        ArgumentException.ThrowIfNullOrWhiteSpace(extension);

        return $"casazen-tassa-soggiorno-riepilogo-{Compact(period.From)}-{Compact(period.To)}.{extension}";
    }

    /// <summary>
    /// The status in Italian, as the file says it. <see cref="TouristTaxSummaryStatus.NothingOwed"/> means that nothing is recorded, not that
    /// nothing is due (a stay with no amount is counted apart), so the file never says «niente da versare» for it.
    /// </summary>
    public static string StatusLabel(TouristTaxSummaryStatus status, TouristTaxCsvTexts texts)
    {
        ArgumentNullException.ThrowIfNull(texts);

        return status switch
        {
            TouristTaxSummaryStatus.NothingOwed => texts.NothingRecorded,
            TouristTaxSummaryStatus.ToPay => "Da versare",
            TouristTaxSummaryStatus.Paid => "Versata",
            TouristTaxSummaryStatus.Overpaid => "Versata in più",
            _ => status.ToString(),
        };
    }

    /// <summary>The note of a row: the incomplete-amount text when it counts stays with no amount, empty otherwise.</summary>
    private static string? IncompleteNote(int staysWithoutAmount, TouristTaxCsvTexts texts) =>
        staysWithoutAmount > 0 ? texts.IncompleteAmount : null;

    /// <summary>A comune of the file: what is owed and, unless the payments are hidden, what was paid, the balance and the status; then the note.</summary>
    private static IReadOnlyList<ReportCell> ComuneRow(ComuneTaxSummary comune, bool paymentsHidden, TouristTaxCsvTexts texts)
    {
        var cells = new List<ReportCell>
        {
            comune.Comune,
            comune.Stays,
            comune.Nights,
            comune.Guests,
            ReportCell.Money(comune.Owed),
            comune.StaysWithoutAmount,
        };
        if (!paymentsHidden)
        {
            cells.Add(ReportCell.Money(comune.Paid));
            cells.Add(ReportCell.Money(comune.Balance));
            cells.Add(comune.Status is { } status ? StatusLabel(status, texts) : null);
        }

        cells.Add(IncompleteNote(comune.StaysWithoutAmount, texts));
        return cells;
    }

    /// <summary>The <c>Totale</c> row: the sums, no status (a total has none), and the note.</summary>
    private static IReadOnlyList<ReportCell> TotalRow(TouristTaxSummaryTotals total, bool paymentsHidden, TouristTaxCsvTexts texts)
    {
        var cells = new List<ReportCell>
        {
            Total,
            total.Stays,
            total.Nights,
            total.Guests,
            ReportCell.Money(total.Owed),
            total.StaysWithoutAmount,
        };
        if (!paymentsHidden)
        {
            cells.Add(ReportCell.Money(total.Paid));
            cells.Add(ReportCell.Money(total.Balance));
            cells.Add(ReportCell.Empty);
        }

        cells.Add(IncompleteNote(total.StaysWithoutAmount, texts));
        return cells;
    }

    private static string Date(DateOnly day) => day.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture);

    private static string Compact(DateOnly day) => day.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
}
