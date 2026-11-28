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

public static class TouristTaxSummaryDocuments
{
    public const string SummaryTitle =
        "Tassa di soggiorno per comune: dovuta sui soggiorni con check-in nel periodo, versata con periodo compreso nel periodo";

    public const string OwedOnlyTitle =
        "Tassa di soggiorno per comune: dovuta sui soggiorni con check-in nel periodo, solo degli immobili dell'ambito di accesso (versamenti non mostrati)";

    public const string RemittancesTitle = "Versamenti registrati";

    public const string ComuniSheet = "Per comune";

    public const string RemittancesSheet = "Versamenti registrati";

    private const string Total = "Totale";

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

    public static byte[] Csv(TouristTaxSummary summary, TouristTaxCsvTexts texts) => CsvWriter.Write(Tables(summary, texts));

    public static byte[] Xlsx(TouristTaxSummary summary, TouristTaxCsvTexts texts)
    {
        var tables = Tables(summary, texts);
        var sheets = new List<ReportSheet> { new(ComuniSheet, tables[0]) };
        if (tables.Count > 1)
            sheets.Add(new ReportSheet(RemittancesSheet, tables[1]));

        return XlsxWriter.Write(sheets);
    }

    public static string FileName(FiscalReportPeriod period, string extension = "csv")
    {
        ArgumentNullException.ThrowIfNull(period);
        ArgumentException.ThrowIfNullOrWhiteSpace(extension);

        return $"casazen-tassa-soggiorno-riepilogo-{Compact(period.From)}-{Compact(period.To)}.{extension}";
    }

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
