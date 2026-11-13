using System.Globalization;
using Casazen.Core.Documents;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;
using Casazen.Core.Utilities;

namespace Casazen.Infrastructure.Services;

/// <summary>
/// The CSV and the Excel file of the list of the fiscal documents (FIS-07, NF-87, E06/F03/US03/T01), in the style of <see cref="FiscalReportDocuments"/>:
/// the same fourteen columns in both files, built once as typed cells (<see cref="Table"/>) and written by <see cref="CsvWriter"/> and
/// <see cref="XlsxWriter"/>, so the two files carry the same documents, the same cents and the same days. The single documents already download as
/// PDF and as FatturaPA XML (<c>GET api/fiscal/documents/{id}/pdf</c> and <c>/xml</c>).
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><b>Stored, never recomputed.</b> The amounts are the ones the document stores, to two places, in the currency of its own column (a credit note
/// carries its positive amounts and the kind says what it is). The day of the issue is a Europe/Rome calendar day, a date cell in the Excel file and
/// <c>yyyy-MM-dd</c> in the CSV.</item>
/// <item><b>Texts</b> (the headers and the words of the kind, the payment state, the SDI state and the source) come from the resource files in the language
/// of the request through <see cref="IDocumentTexts"/>; the CSV is the Italian file of <see cref="CsvWriter"/> (UTF-8 with BOM, <c>;</c>, decimal comma)
/// whatever the language. The channel manager is never named: its documents say «Channel manager».</item>
/// <item><b>Privacy.</b> The only data of the recipient is its name (what the list already shows to whoever reads the payments); the fiscal code, the
/// VAT number and the address are not in the file.</item>
/// <item><b>Never a formula.</b> A text that a spreadsheet would run (<c>= + - @</c> first: a property or a recipient is typed by people) is written with a
/// leading apostrophe in the CSV and as an inline string cell in the Excel file.</item>
/// </list>
/// </remarks>
public static class FiscalDocumentExports
{
    /// <summary>
    /// The most documents read for a file: as many as the rows the two writers accept, so a list one document too long is refused by them
    /// (422 <c>csv_too_many_rows</c> or <c>xlsx_too_many_rows</c>) and never cut without a word.
    /// </summary>
    public const int Limit = CsvWriter.MaxRows;

    /// <summary>The headers of the fourteen columns, in the language: kind, number, issue date, booking code, property, recipient, taxable base, VAT, stamp duty, total, currency, payment state, SDI state, source.</summary>
    public static IReadOnlyList<string> Header(IDocumentTexts texts, string language)
    {
        ArgumentNullException.ThrowIfNull(texts);

        return
        [
            texts.Get("FiscalExportColKind", language),
            texts.Get("FiscalExportColNumber", language),
            texts.Get("FiscalExportColIssueDate", language),
            texts.Get("FiscalExportColBookingCode", language),
            texts.Get("FiscalExportColProperty", language),
            texts.Get("FiscalExportColRecipient", language),
            texts.Get("FiscalExportColTaxableBase", language),
            texts.Get("FiscalExportColVat", language),
            texts.Get("FiscalExportColStampDuty", language),
            texts.Get("FiscalExportColTotal", language),
            texts.Get("FiscalExportColCurrency", language),
            texts.Get("FiscalExportColPaymentState", language),
            texts.Get("FiscalExportColSdiState", language),
            texts.Get("FiscalExportColSource", language),
        ];
    }

    /// <summary>The rows as typed cells, one list per document, in the order of <see cref="Header"/>: text, a day, amounts, text; an empty cell is <c>null</c>.</summary>
    public static IReadOnlyList<IReadOnlyList<object?>> Table(IReadOnlyList<FiscalDocumentExportRow> rows, IDocumentTexts texts, string language)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(texts);

        return rows.Select(row => (IReadOnlyList<object?>)new object?[]
            {
                FiscalDocumentPdfs.KindLabel(row.Kind, texts, language),
                row.Number,
                row.IssueDate,
                row.BookingCode is { Length: > 0 } code ? BookingCodes.Format(code) : null,
                row.PropertyName,
                row.RecipientName,
                row.TaxableBase,
                row.VatAmount,
                row.StampDuty,
                row.Total,
                row.Currency,
                PaymentLabel(row.PaymentState, texts, language),
                SdiLabel(row.SdiStatus, texts, language),
                SourceLabel(row.Source, texts, language),
            }).ToList();
    }

    /// <summary>The CSV of the list: the header, then a row for each document (the Italian file of <see cref="CsvWriter"/>).</summary>
    /// <exception cref="Casazen.Core.Exceptions.DomainRuleException">There are more rows than a file holds (422 <c>csv_too_many_rows</c>).</exception>
    public static byte[] Csv(IReadOnlyList<FiscalDocumentExportRow> rows, IDocumentTexts texts, string language) =>
        CsvWriter.Write(Table(rows, texts, language).Prepend(Header(texts, language)));

    /// <summary>The Excel file of the list: one sheet, the same header and rows as the CSV, amounts as numbers and days as dates.</summary>
    /// <exception cref="Casazen.Core.Exceptions.DomainRuleException">There are more rows than a file holds (422 <c>xlsx_too_many_rows</c>).</exception>
    public static byte[] Xlsx(IReadOnlyList<FiscalDocumentExportRow> rows, IDocumentTexts texts, string language) =>
        XlsxWriter.Write(texts.Get("FiscalExportSheetName", language), Header(texts, language), Table(rows, texts, language));

    /// <summary>The name of the file: <c>documenti-2026-10.csv</c>, the month being the one of <paramref name="month"/> (a calendar day of it).</summary>
    public static string FileName(DateOnly month, string extension)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(extension);

        return string.Create(CultureInfo.InvariantCulture, $"documenti-{month:yyyy-MM}.{extension}");
    }

    private static string PaymentLabel(FiscalDocumentPaymentState state, IDocumentTexts texts, string language) => state switch
    {
        FiscalDocumentPaymentState.Paid => texts.Get("FiscalExportPaymentPaid", language),
        FiscalDocumentPaymentState.ToCollect => texts.Get("FiscalExportPaymentToCollect", language),
        _ => texts.Get("FiscalExportPaymentUnknown", language),
    };

    private static string SdiLabel(string status, IDocumentTexts texts, string language) => status switch
    {
        FiscalDocumentSdiStatuses.NotApplicable => texts.Get("FiscalExportSdiNotApplicable", language),
        FiscalDocumentSdiStatuses.ToSend => texts.Get("FiscalExportSdiToSend", language),
        FiscalDocumentSdiStatuses.Submitted => texts.Get("FiscalExportSdiSubmitted", language),
        FiscalDocumentSdiStatuses.Delivered => texts.Get("FiscalExportSdiDelivered", language),
        FiscalDocumentSdiStatuses.Rejected => texts.Get("FiscalExportSdiRejected", language),
        FiscalDocumentSdiStatuses.ManualDeclared => texts.Get("FiscalExportSdiManualDeclared", language),
        _ => texts.Get("FiscalExportSdiUnknown", language),
    };

    private static string SourceLabel(FiscalDocumentSource source, IDocumentTexts texts, string language) => source switch
    {
        FiscalDocumentSource.Octorate => texts.Get("FiscalExportSourceChannelManager", language),
        _ => texts.Get("FiscalExportSourceCasaZen", language),
    };
}
