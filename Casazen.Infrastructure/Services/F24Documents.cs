using System.Globalization;
using Casazen.Core.Documents;
using Casazen.Core.Entities;
using Casazen.Core.Services;
using Casazen.Core.Utilities;

namespace Casazen.Infrastructure.Services;

/// <summary>
/// The files of the F24 data of a month (NF-25, ADR-018, E06/F01/US01/T02, E18/F01/US01/T02). The PDF, in the style of <see cref="FiscalReportDocuments"/>: the stored
/// <see cref="F24Record"/> as it is (tax code, period, amount, due date, payment, filing state and receipt, E06/F05/US02/T03), with the labels of the resource files. The
/// CSV and the Excel file (<see cref="Csv"/>, <see cref="Xlsx"/>): one line per tax code and period with the amount and the due date, then the breakdown per owner and
/// payout, from <see cref="IWithholdingService.GetF24DataAsync"/>. All of them are the data to fill the form with, never the form that was filed, and never a telematic file.
/// </summary>
public static class F24Documents
{
    /// <summary>
    /// The name of the sheet of the lines of the form: «F24» in both languages, so it is not a text to translate (the localization test wants a different English
    /// text for every key of the resource files).
    /// </summary>
    public const string LinesSheetName = "F24";

    public static PdfDocumentContent Pdf(F24Record record, string orgName, IDocumentTexts texts, string language)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(texts);

        var period = $"{record.ReferenceMonth.ToString("00", CultureInfo.InvariantCulture)}/{record.ReferenceYear.ToString(CultureInfo.InvariantCulture)}";
        var blocks = new List<PdfBlock>
        {
            new PdfParagraph(texts.Get("F24DocNotFiled", language), Bold: true),
            new PdfParagraph(texts.Get("F24DocOrg", language, orgName)),
            new PdfParagraph(texts.Get("F24DocPeriod", language, period)),
            new PdfHeading(texts.Get("F24DocSectionErario", language)),
            new PdfTable(
                [
                    new PdfTableColumn(texts.Get("F24DocTaxCode", language), 1.1),
                    new PdfTableColumn(texts.Get("F24DocColMonth", language), 1.1),
                    new PdfTableColumn(texts.Get("F24DocColYear", language), 1.1),
                    new PdfTableColumn(texts.Get("F24DocAmount", language), 1.4, PdfCellAlignment.Right),
                ],
                [
                    [
                        record.TaxCode,
                        record.ReferenceMonth.ToString("00", CultureInfo.InvariantCulture),
                        record.ReferenceYear.ToString(CultureInfo.InvariantCulture),
                        DocumentLanguages.Money(record.TotalAmount, language),
                    ],
                ]),
            new PdfParagraph(texts.Get("F24DocDueDate", language, DocumentLanguages.Day(DateOnly.FromDateTime(record.DueDate)))),
            new PdfParagraph(record.PaidAt is { } paid
                ? texts.Get("F24DocPaidAt", language, DocumentLanguages.Day(DateOnly.FromDateTime(paid)))
                : texts.Get("F24DocNotPaid", language)),
            new PdfParagraph(texts.Get("F24DocFilingState", language, texts.Get("TaxFilingState" + record.FilingState, language))),
            new PdfParagraph(texts.Get("F24DocDisclaimer", language)),
        };
        if (record.ReceiptReference is { } receipt)
            blocks.Insert(blocks.Count - 1, new PdfParagraph(texts.Get("F24DocReceipt", language, receipt)));

        return new PdfDocumentContent(texts.Get("F24DocTitle", language, period), blocks)
        {
            Language = DocumentLanguages.Normalize(language) == DocumentLanguages.English ? "en-GB" : "it-IT",
        };
    }

    /// <summary>
    /// The two tables of the export of a month: the lines of the section Erario (tax code, reference month and year, amount in euro, due date, the decisions that make
    /// it), then the breakdown per owner and payout (owner, reference and day of the payout, decision or reversal, tax code, rule version, base, rate and withholding in
    /// euro, then the currency, gross, withholding kept and net of the payout). The amounts are the stored ones, never computed here; a reversal has the opposite amounts.
    /// </summary>
    public static IReadOnlyList<ReportTable> ExportTables(F24MonthData data, IDocumentTexts texts, string language)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(texts);

        var period = string.Create(CultureInfo.InvariantCulture, $"{data.Month:00}/{data.Year:0000}");
        var lines = new ReportTable(
            texts.Get("F24ExportTitle", language, period),
            [
                texts.Get("F24DocTaxCode", language),
                texts.Get("F24DocColMonth", language),
                texts.Get("F24DocColYear", language),
                texts.Get("F24DocAmount", language),
                texts.Get("AccPkgColDueDate", language),
                texts.Get("AccPkgColEvidence", language),
            ]);
        foreach (var line in data.Lines)
        {
            lines.Add(
                line.TaxCode,
                line.ReferenceMonth.ToString("00", CultureInfo.InvariantCulture),
                line.ReferenceYear,
                ReportCell.Money(line.Amount),
                ReportCell.Date(line.DueDate),
                line.EvidenceCount);
        }

        var breakdown = new ReportTable(
            texts.Get("F24ExportBreakdownTitle", language, period),
            [
                texts.Get("AccPkgColOwner", language),
                texts.Get("F24ExportColReference", language),
                texts.Get("AccPkgColPaidOn", language),
                texts.Get("F24ExportColKind", language),
                texts.Get("F24DocTaxCode", language),
                texts.Get("AccPkgColRuleVersion", language),
                texts.Get("F24ExportColGrossBaseEuro", language),
                texts.Get("AccPkgColRate", language),
                texts.Get("F24ExportColWithheldEuro", language),
                texts.Get("F24ExportColPayoutCurrency", language),
                texts.Get("F24ExportColPayoutGross", language),
                texts.Get("F24ExportColPayoutWithheld", language),
                texts.Get("F24ExportColPayoutNet", language),
            ]);
        foreach (var row in data.Breakdown)
        {
            breakdown.Add(
                row.OwnerName,
                row.PayoutReference,
                ReportCell.Date(row.PaidOn),
                texts.Get(row.Reversal ? "F24ExportKindReversal" : "F24ExportKindWithholding", language),
                row.TaxCode,
                row.RuleVersion,
                ReportCell.Money(row.GrossBase),
                ReportCell.Percent(row.RatePercent / 100m),
                ReportCell.Money(row.Amount),
                row.PayoutCurrency,
                ReportCell.Money(row.PayoutGross),
                ReportCell.Money(row.PayoutWithheld),
                ReportCell.Money(row.PayoutNet));
        }

        return [lines, breakdown];
    }

    /// <summary>The CSV of the export: the two tables one under the other (<see cref="CsvWriter"/>: decimal comma, <c>;</c>, a day as <c>dd/MM/yyyy</c>).</summary>
    public static byte[] Csv(F24MonthData data, IDocumentTexts texts, string language) => CsvWriter.Write(ExportTables(data, texts, language));

    /// <summary>The Excel file of the export: a sheet for the lines of the form and one for the breakdown (<see cref="XlsxWriter"/>).</summary>
    public static byte[] Xlsx(F24MonthData data, IDocumentTexts texts, string language)
    {
        var tables = ExportTables(data, texts, language);
        return XlsxWriter.Write([new ReportSheet(LinesSheetName, tables[0]), new ReportSheet(texts.Get("F24ExportSheetBreakdown", language), tables[1])]);
    }

    /// <summary>The name of the file of the export of a month: <c>f24-2026-11.csv</c>.</summary>
    public static string ExportFileName(int year, int month, string extension) =>
        string.Create(CultureInfo.InvariantCulture, $"f24-{year:0000}-{month:00}.{extension}");
}
