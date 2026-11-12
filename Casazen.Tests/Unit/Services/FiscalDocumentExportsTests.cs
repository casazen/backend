using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Xml.Linq;
using Casazen.Core.BankStatements;
using Casazen.Core.Documents;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Services;
using Casazen.Infrastructure.Services;
using Casazen.Web.Resources;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// E06/F03/US03/T01 (FIS-07, NF-87): the list of the fiscal documents as CSV and as Excel has the same fourteen columns in both files, carries the same
/// cents and the same days, says its words in the language of the request, never names the channel manager, and never lets a spreadsheet run a text
/// somebody typed.
/// </summary>
public class FiscalDocumentExportsTests
{
    private static readonly XNamespace Main = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private static readonly CultureInfo Italian = CultureInfo.GetCultureInfo("it-IT");
    private static readonly IDocumentTexts Texts = new ResxDocumentTexts();

    private static readonly string[] ItalianHeader =
    [
        "Tipo", "Numero", "Data di emissione", "Codice prenotazione", "Immobile", "Destinatario", "Imponibile", "IVA", "Imposta di bollo", "Totale", "Valuta",
        "Stato del pagamento", "Stato SDI", "Origine",
    ];

    private static readonly string[] EnglishHeader =
    [
        "Type", "Number", "Issue date", "Booking code", "Property", "Recipient", "Taxable amount", "VAT", "Stamp duty", "Total", "Currency",
        "Payment status", "SDI status", "Source",
    ];

    /// <summary>Data rule: a text a spreadsheet would run (<c>= + - @</c> first) is written as text in the CSV (a leading apostrophe) and in the Excel file (an inline string, no formula).</summary>
    [Fact]
    public void DocumentExport_FormulaInjection_Neutralized_Test()
    {
        string[] prefixes = ["=", "+", "-", "@"];
        var rows = prefixes
            .Select(p => Row(number: p + "1", property: p + "SUM(A1)", recipient: p + "cmd|' /C calc'!A0"))
            .ToList();

        // CSV: every one of the three typed columns carries a leading apostrophe, so the cell is text.
        var csv = CsvCells(FiscalDocumentExports.Csv(rows, Texts, "it"));
        for (var i = 0; i < prefixes.Length; i++)
        {
            var cells = csv[i + 1];
            Assert.Equal("'" + prefixes[i] + "1", cells[1]);
            Assert.Equal("'" + prefixes[i] + "SUM(A1)", cells[4]);
            Assert.Equal("'" + prefixes[i] + "cmd|' /C calc'!A0", cells[5]);
        }

        // Excel: the same cells are inline strings, read back as the text typed and never as a number, and the sheet holds no formula element at all.
        var workbook = FiscalDocumentExports.Xlsx(rows, Texts, "it");
        var table = XlsxTableReader.Read(workbook);
        for (var i = 0; i < prefixes.Length; i++)
        {
            foreach (var (column, expected) in new[] { (1, prefixes[i] + "1"), (4, prefixes[i] + "SUM(A1)"), (5, prefixes[i] + "cmd|' /C calc'!A0") })
            {
                Assert.Equal(new Cell(expected, false), table[i + 1][column]);
            }
        }

        var sheet = SheetOf(workbook);
        Assert.Empty(sheet.Descendants(Main + "f"));
        foreach (var column in new[] { "B", "E", "F" })
        {
            foreach (var row in Enumerable.Range(2, prefixes.Length))
            {
                var cell = sheet.Descendants(Main + "c").Single(c => (string?)c.Attribute("r") == column + row);
                Assert.Equal("inlineStr", (string?)cell.Attribute("t"));
            }
        }
    }

    /// <summary>Money rule: the two files carry the same cents (the amounts as the documents store them), the same days and the same words, row by row.</summary>
    [Fact]
    public void DocumentExport_CsvAndXlsx_CarryTheSameRowsAndAmounts_Test()
    {
        var rows = new List<FiscalDocumentExportRow>
        {
            Row(taxable: 330.18m, vat: 31.82m, stamp: 0m, total: 362.00m, issued: new DateOnly(2026, 11, 12)),
            Row(kind: FiscalDocumentKind.CreditNote, number: "2026/2", taxable: 0.10m, vat: 0.01m, stamp: 2.00m, total: 2.11m, currency: "CHF", issued: new DateOnly(2026, 12, 31)),
            Row(number: null, bookingCode: null, property: null, recipient: null, taxable: 1234567.89m, vat: 0m, stamp: 0m, total: 1234567.89m) with { IssueDate = null },
        };

        var csv = CsvCells(FiscalDocumentExports.Csv(rows, Texts, "it"));
        var table = XlsxTableReader.Read(FiscalDocumentExports.Xlsx(rows, Texts, "it"));

        Assert.Equal(rows.Count + 1, csv.Length);
        Assert.Equal(csv.Length, table.Count);
        for (var r = 0; r < rows.Count; r++)
        {
            var row = rows[r];
            var cells = csv[r + 1];
            var sheetCells = table[r + 1];
            Assert.Equal(14, cells.Length);

            // The four amounts, to the cent, as the document stores them: a decimal comma in the CSV, a number cell in the Excel file.
            decimal[] stored = [row.TaxableBase, row.VatAmount, row.StampDuty, row.Total];
            for (var c = 0; c < stored.Length; c++)
            {
                Assert.Equal(stored[c], decimal.Parse(cells[6 + c], NumberStyles.Number, Italian));
                Assert.True(sheetCells[6 + c].IsNumber);
                Assert.Equal(stored[c], decimal.Parse(sheetCells[6 + c].Text, NumberStyles.Number, CultureInfo.InvariantCulture));
            }

            // The day: an ISO day in the CSV, the serial of the same day in the Excel file; none in both when the document has none.
            if (row.IssueDate is { } day)
            {
                Assert.Equal(day, DateOnly.ParseExact(cells[2], "yyyy-MM-dd", CultureInfo.InvariantCulture));
                Assert.True(sheetCells[2].IsNumber);
                Assert.Equal(day, DateOnly.FromDayNumber(new DateOnly(1899, 12, 30).DayNumber + int.Parse(sheetCells[2].Text, CultureInfo.InvariantCulture)));
            }
            else
            {
                Assert.Equal(string.Empty, cells[2]);
                Assert.Equal(string.Empty, sheetCells[2].Text);
            }

            // Every other cell says the same words.
            foreach (var c in new[] { 0, 1, 3, 4, 5, 10, 11, 12, 13 })
            {
                Assert.Equal(cells[c], c < sheetCells.Length ? sheetCells[c].Text : string.Empty);
            }
        }
    }

    [Fact]
    public void Header_IsTheFourteenColumnsOfTheBrief_InBothFilesAndBothLanguages()
    {
        Assert.Equal(ItalianHeader, FiscalDocumentExports.Header(Texts, "it"));
        Assert.Equal(EnglishHeader, FiscalDocumentExports.Header(Texts, "en"));
        Assert.Equal(ItalianHeader, CsvCells(FiscalDocumentExports.Csv([], Texts, "it")).Single());
        Assert.Equal(EnglishHeader, CsvCells(FiscalDocumentExports.Csv([], Texts, "en")).Single());
        Assert.Equal(ItalianHeader, XlsxTableReader.Read(FiscalDocumentExports.Xlsx([], Texts, "it")).Single().Select(c => c.Text));
        Assert.Equal(EnglishHeader, XlsxTableReader.Read(FiscalDocumentExports.Xlsx([], Texts, "en")).Single().Select(c => c.Text));
    }

    [Fact]
    public void Words_FollowTheLanguage_AndTheChannelManagerIsNeverNamed()
    {
        var rows = new List<FiscalDocumentExportRow>
        {
            Row(FiscalDocumentKind.Invoice, payment: FiscalDocumentPaymentState.Paid, sdi: FiscalDocumentSdiStatuses.Delivered),
            Row(FiscalDocumentKind.Receipt, payment: FiscalDocumentPaymentState.ToCollect, sdi: FiscalDocumentSdiStatuses.NotApplicable),
            Row(FiscalDocumentKind.CreditNote, payment: FiscalDocumentPaymentState.Unknown, sdi: FiscalDocumentSdiStatuses.Rejected, source: FiscalDocumentSource.Octorate),
            Row(FiscalDocumentKind.Proforma, sdi: FiscalDocumentSdiStatuses.ManualDeclared, source: FiscalDocumentSource.Octorate),
            Row(sdi: FiscalDocumentSdiStatuses.Submitted),
            Row(sdi: FiscalDocumentSdiStatuses.ToSend),
            Row(sdi: FiscalDocumentSdiStatuses.Unknown),
        };

        var italian = CsvCells(FiscalDocumentExports.Csv(rows, Texts, "it")).Skip(1).ToList();
        var english = CsvCells(FiscalDocumentExports.Csv(rows, Texts, "en")).Skip(1).ToList();

        Assert.Equal(["Fattura", "Pagato", "Consegnata", "Emesso da CasaZen"], new[] { italian[0][0], italian[0][11], italian[0][12], italian[0][13] });
        Assert.Equal(["Ricevuta", "Da incassare", "Non applicabile", "Emesso da CasaZen"], new[] { italian[1][0], italian[1][11], italian[1][12], italian[1][13] });
        Assert.Equal(["Nota di credito", "Non indicato", "Scartata", "Emesso dal tuo channel manager"], new[] { italian[2][0], italian[2][11], italian[2][12], italian[2][13] });
        Assert.Equal(["Fattura proforma", "Inviata fuori da CasaZen", "Emesso dal tuo channel manager"], new[] { italian[3][0], italian[3][12], italian[3][13] });
        Assert.Equal(["Inviata", "Da inviare", "Non indicato"], new[] { italian[4][12], italian[5][12], italian[6][12] });
        Assert.Equal(["Invoice", "Paid", "Delivered", "Issued by CasaZen"], new[] { english[0][0], english[0][11], english[0][12], english[0][13] });
        Assert.Equal(["Receipt", "To collect", "Not applicable", "Issued by CasaZen"], new[] { english[1][0], english[1][11], english[1][12], english[1][13] });
        Assert.Equal(["Credit note", "Not stated", "Rejected", "Issued by your channel manager"], new[] { english[2][0], english[2][11], english[2][12], english[2][13] });
        Assert.Equal(["Pro forma invoice", "Sent outside CasaZen", "Issued by your channel manager"], new[] { english[3][0], english[3][12], english[3][13] });
        Assert.Equal(["Sent", "To send", "Not stated"], new[] { english[4][12], english[5][12], english[6][12] });

        // The channel manager is never named in a file, in either format or language.
        foreach (var language in new[] { "it", "en" })
        {
            Assert.DoesNotContain("octorate", Encoding.UTF8.GetString(FiscalDocumentExports.Csv(rows, Texts, language)), StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("octorate", string.Concat(XlsxTableReader.Read(FiscalDocumentExports.Xlsx(rows, Texts, language)).SelectMany(r => r.Select(c => c.Text))), StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void TheBookingCodeIsShownAsThePeopleReadIt_AndADocumentWithoutABookingHasNone()
    {
        var csv = CsvCells(FiscalDocumentExports.Csv([Row(bookingCode: "ABCDEFGHJK"), Row(bookingCode: null)], Texts, "it"));

        Assert.Equal("ABCDE-FGHJK", csv[1][3]);
        Assert.Equal(string.Empty, csv[2][3]);
    }

    /// <summary>A real day formatted, whatever the culture of the server: the name of the file is the month, as <c>yyyy-MM</c>.</summary>
    [Fact]
    public void FileName_IsTheMonthOfTheDay_WhateverTheCultureOfTheServer()
    {
        var culture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("ar-SA");

            Assert.Equal("documenti-2026-10.csv", FiscalDocumentExports.FileName(new DateOnly(2026, 10, 31), "csv"));
            Assert.Equal("documenti-2026-01.xlsx", FiscalDocumentExports.FileName(new DateOnly(2026, 1, 1), "xlsx"));
        }
        finally
        {
            CultureInfo.CurrentCulture = culture;
        }
    }

    /// <summary>A list one document too long is refused by the two writers (422) and never cut without a word; one short of it is a file.</summary>
    [Fact]
    public void ATooLongList_IsRefusedByBothFormats_AndOneShortOfItIsAFile()
    {
        var tooMany = Enumerable.Repeat(Row(), FiscalDocumentExports.Limit).ToList();
        var fits = tooMany.Take(FiscalDocumentExports.Limit - 1).ToList();

        Assert.Equal("csv_too_many_rows", Assert.Throws<DomainRuleException>(() => FiscalDocumentExports.Csv(tooMany, Texts, "it")).Code);
        Assert.Equal("xlsx_too_many_rows", Assert.Throws<DomainRuleException>(() => FiscalDocumentExports.Xlsx(tooMany, Texts, "it")).Code);
        Assert.Equal(FiscalDocumentExports.Limit, CsvCells(FiscalDocumentExports.Csv(fits, Texts, "it")).Length);
        Assert.Equal(FiscalDocumentExports.Limit, XlsxTableReader.Read(FiscalDocumentExports.Xlsx(fits, Texts, "it")).Count);
    }

    /// <summary>
    /// Every key of the export has a text in both languages that is not the key and is not the same in the two (the rule of the audit of the resource files,
    /// which runs in the integration suite: a word identical in Italian and English fails it).
    /// </summary>
    [Fact]
    public void ResourceKeys_AreTranslated_AndNeverTheSameInBothLanguages()
    {
        string[] keys =
        [
            "FiscalExportSheetName", "FiscalExportColKind", "FiscalExportColNumber", "FiscalExportColIssueDate", "FiscalExportColBookingCode", "FiscalExportColProperty",
            "FiscalExportColRecipient", "FiscalExportColTaxableBase", "FiscalExportColVat", "FiscalExportColStampDuty", "FiscalExportColTotal", "FiscalExportColCurrency",
            "FiscalExportColPaymentState", "FiscalExportColSdiState", "FiscalExportColSource", "FiscalExportPaymentPaid", "FiscalExportPaymentToCollect",
            "FiscalExportPaymentUnknown", "FiscalExportSdiNotApplicable", "FiscalExportSdiToSend", "FiscalExportSdiSubmitted", "FiscalExportSdiDelivered",
            "FiscalExportSdiRejected", "FiscalExportSdiManualDeclared", "FiscalExportSdiUnknown", "FiscalExportSourceCasaZen", "FiscalExportSourceChannelManager",
            "FiscalExportInvalidFormat", "FiscalExportInvalidRange",
        ];

        foreach (var key in keys)
        {
            var italian = Texts.Get(key, "it");
            var english = Texts.Get(key, "en");

            Assert.NotEqual(key, italian);
            Assert.NotEqual(key, english);
            Assert.NotEqual(italian, english);
        }
    }

    private static FiscalDocumentExportRow Row(
        FiscalDocumentKind kind = FiscalDocumentKind.Invoice,
        string? number = "2026/1",
        DateOnly? issued = null,
        string? bookingCode = "ABCDEFGHJK",
        string? property = "TEST Casa",
        string? recipient = "TEST Cliente Srl",
        decimal taxable = 330.18m,
        decimal vat = 31.82m,
        decimal stamp = 0m,
        decimal total = 362.00m,
        string currency = "EUR",
        FiscalDocumentPaymentState payment = FiscalDocumentPaymentState.ToCollect,
        string sdi = FiscalDocumentSdiStatuses.ToSend,
        FiscalDocumentSource source = FiscalDocumentSource.CasaZen) =>
        new(Guid.NewGuid(), kind, number, issued ?? new DateOnly(2026, 11, 12), bookingCode, property, recipient, taxable, vat, stamp, total, currency, payment, sdi, source);

    /// <summary>The rows of a CSV file of test data (no cell holds a separator or a quote), without the byte order mark.</summary>
    private static string[][] CsvCells(byte[] file) =>
        Encoding.UTF8.GetString(file, 3, file.Length - 3).TrimEnd('\r', '\n').Split("\r\n").Select(line => line.Split(';')).ToArray();

    private static XDocument SheetOf(byte[] workbook)
    {
        using var zip = new ZipArchive(new MemoryStream(workbook, writable: false), ZipArchiveMode.Read);
        using var stream = zip.GetEntry("xl/worksheets/sheet1.xml")!.Open();
        return XDocument.Load(stream);
    }
}
