using System.Globalization;
using Casazen.Core.Documents;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;
using Casazen.Core.Utilities;

namespace Casazen.Infrastructure.Services;

/// <summary>
/// The files of the tax reporting of the owners (E18/F01/US02, NF-117, ⚖ T2): the data of the Certificazione Unica (a CSV or an Excel file with one row for each recipient and one for each of
/// its stays, and the draft «prospetto CU» of an owner as a PDF), the data of the 770 (the withholdings made and paid, month by month) and the communication of the short-let contracts handled
/// without withholding. They carry <b>every field the accountant's software needs</b>, from data that was decided and stored before (the annual data, the evidence, the F24 records): nothing is
/// computed here, nothing is a telematic file or a form filed, and nothing is sent. The texts are those of the resource files, in the language of the request. The CSV and the Excel file of a
/// report have the same tables, so the same figures (<see cref="ReportTable"/>).
/// </summary>
public static class TaxReportingExports
{
    /// <summary>The name of the sheet of the recipients of the Certificazione Unica: «CU» in both languages, so it is not a text to translate.</summary>
    public const string CuRecordsSheetName = "CU";

    /// <summary>The name of the sheet of the data of the 770: «770» in both languages.</summary>
    public const string Tax770SheetName = "770";

    /// <summary>The most rows of a table of these exports: a year of an agency is far below it, and a file is never cut at the 10 000 rows of the other reports.</summary>
    public const int MaxRows = 200_000;

    // ---------------------------------------------------------------- the Certificazione Unica

    /// <summary>
    /// The two tables of the Certificazione Unica data of a year: the recipients (who it is made out to, the base and the withholding in euro, the tax code, how the record was made and what is
    /// missing of the recipient), then the stays of each recipient (the property with its address, cadastral data and CIN, the dates and the nights, the base and the withholding of the stay).
    /// A reversal is a row with the opposite amounts; every amount is the stored one.
    /// </summary>
    public static IReadOnlyList<ReportTable> CuTables(int year, IReadOnlyList<CuRecord> records, IDocumentTexts texts, string language)
    {
        ArgumentNullException.ThrowIfNull(records);
        ArgumentNullException.ThrowIfNull(texts);

        var period = year.ToString("0000", CultureInfo.InvariantCulture);
        var recipients = new ReportTable(
            texts.Get("CuExportTitle", language, period),
            [
                texts.Get("CuColRecipient", language),
                texts.Get("CuColFirstName", language),
                texts.Get("CuColLastName", language),
                texts.Get("CuColEntityKind", language),
                texts.Get("CuColFiscalCode", language),
                texts.Get("CuColVatNumber", language),
                texts.Get("CuColResidence", language),
                texts.Get("CuColStreet", language),
                texts.Get("CuColCivic", language),
                texts.Get("CuColPostalCode", language),
                texts.Get("CuColCity", language),
                texts.Get("CuColProvince", language),
                texts.Get("CuColCountry", language),
                texts.Get("CuColLegalAddress", language),
                texts.Get("F24ExportColGrossBaseEuro", language),
                texts.Get("F24ExportColWithheldEuro", language),
                texts.Get("F24DocTaxCode", language),
                texts.Get("CuColAttribution", language),
                texts.Get("CuColShares", language),
                texts.Get("AccPkgColRuleVersion", language),
                texts.Get("AccPkgColEvidence", language),
                texts.Get("CuColMissing", language),
            ]);
        foreach (var record in records)
        {
            var recipient = record.Recipient ?? new CuRecipient();
            recipients.Add(
                recipient.Name,
                recipient.FirstName,
                recipient.LastName,
                EntityKindText(recipient.EntityKind, texts, language),
                recipient.FiscalCode,
                recipient.VatNumber,
                recipient.TaxResidenceCountry,
                recipient.Street,
                recipient.Civic,
                recipient.PostalCode,
                recipient.City,
                recipient.Province,
                recipient.Country,
                recipient.LegalAddress,
                ReportCell.Money(record.GrossBase),
                ReportCell.Money(record.Withheld),
                record.TaxCode,
                AttributionText(record.Attribution, texts, language),
                SharesText(record.Shares, language),
                string.Join(", ", record.RuleVersions),
                record.Evidence,
                MissingText(recipient.Missing, texts, language));
        }

        var stays = new ReportTable(
            texts.Get("CuExportLinesTitle", language, period),
            [
                texts.Get("CuColRecipient", language),
                texts.Get("CuColFiscalCode", language),
                texts.Get("CuColProperty", language),
                texts.Get("CuColPropertyAddress", language),
                texts.Get("CuColPropertyCity", language),
                texts.Get("CuColIstat", language),
                texts.Get("CuColCin", language),
                texts.Get("CuColCadastralSheet", language),
                texts.Get("CuColCadastralParcel", language),
                texts.Get("CuColCadastralSubaltern", language),
                texts.Get("CuColCadastralCategory", language),
                texts.Get("CuColCheckIn", language),
                texts.Get("CuColCheckOut", language),
                texts.Get("CuColNights", language),
                texts.Get("F24DocColMonth", language),
                texts.Get("CuColCoOwner", language),
                texts.Get("F24ExportColGrossBaseEuro", language),
                texts.Get("F24ExportColWithheldEuro", language),
            ]);
        foreach (var record in records)
        {
            var recipient = record.Recipient ?? new CuRecipient();
            foreach (var line in record.Lines)
            {
                var property = line.PropertyId is { } propertyId ? record.Properties.FirstOrDefault(p => p.Id == propertyId) : null;
                stays.Add(
                    recipient.Name,
                    recipient.FiscalCode,
                    property?.Name,
                    AddressOf(property),
                    CityOf(property),
                    property?.ComuneIstatCode,
                    property?.Cin,
                    property?.CadastralSheet,
                    property?.CadastralParcel,
                    property?.CadastralSubaltern,
                    property?.CadastralCategory,
                    ReportCell.Date(line.CheckIn),
                    ReportCell.Date(line.CheckOut),
                    ReportCell.Integer(line.Nights),
                    ReportCell.Integer(line.Month),
                    record.Shares.FirstOrDefault(s => s.OwnerId == line.SourceOwnerId)?.Name,
                    ReportCell.Money(line.GrossBase),
                    ReportCell.Money(line.Withheld));
            }
        }

        return [recipients, stays];
    }

    /// <summary>The CSV of the Certificazione Unica data: the recipients, then the stays (<see cref="CsvWriter"/>: decimal comma, <c>;</c>, a day as <c>dd/MM/yyyy</c>).</summary>
    public static byte[] CuCsv(int year, IReadOnlyList<CuRecord> records, IDocumentTexts texts, string language) =>
        CsvWriter.Write(CuTables(year, records, texts, language), MaxRows);

    /// <summary>The Excel file of the Certificazione Unica data: a sheet for the recipients (<see cref="CuRecordsSheetName"/>) and one for the stays.</summary>
    public static byte[] CuXlsx(int year, IReadOnlyList<CuRecord> records, IDocumentTexts texts, string language)
    {
        var tables = CuTables(year, records, texts, language);
        return XlsxWriter.Write([new ReportSheet(CuRecordsSheetName, tables[0]), new ReportSheet(texts.Get("CuExportSheetStays", language), tables[1])], MaxRows);
    }

    /// <summary>The name of the file of the Certificazione Unica data of a year: <c>cu-2026.csv</c>.</summary>
    public static string CuFileName(int year, string extension) =>
        string.Create(CultureInfo.InvariantCulture, $"cu-{year:0000}.{extension}");

    /// <summary>
    /// The draft «prospetto CU» of a recipient as a PDF: who it is made out to, the year, the stays with the base and the withholding in euro, the totals, the tax code and the rule, the
    /// co-owners it adds up and what is missing, with the state of the annual data. It says on every page that it is a draft and not the Certificazione Unica: the agency or its accountant
    /// prepares and files that (nothing is filed by CasaZen).
    /// </summary>
    public static PdfDocumentContent CuProspect(int year, CuRecord record, TaxFilingState state, string orgName, IDocumentTexts texts, string language)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(texts);

        var recipient = record.Recipient ?? new CuRecipient();
        var period = year.ToString("0000", CultureInfo.InvariantCulture);
        var blocks = new List<PdfBlock>
        {
            new PdfParagraph(texts.Get("CuProspectDisclaimer", language), Bold: true),
            new PdfParagraph(texts.Get("CuProspectOrg", language, orgName)),
            new PdfParagraph(texts.Get("CuProspectYear", language, period)),
            new PdfParagraph(texts.Get("CuProspectState", language, texts.Get("TaxFilingState" + state, language))),
            new PdfHeading(texts.Get("CuColRecipient", language)),
            new PdfParagraph(texts.Get("CuProspectName", language, recipient.Name ?? "-")),
        };
        if (recipient.FiscalCode is { } fiscalCode)
            blocks.Add(new PdfParagraph(texts.Get("CuProspectFiscalCode", language, fiscalCode)));
        if (recipient.VatNumber is { } vatNumber)
            blocks.Add(new PdfParagraph(texts.Get("CuProspectVat", language, vatNumber)));
        if (AddressText(recipient) is { } address)
            blocks.Add(new PdfParagraph(texts.Get("CuProspectAddress", language, address)));
        if (recipient.Missing.Count > 0)
            blocks.Add(new PdfParagraph(texts.Get("CuProspectMissing", language, MissingText(recipient.Missing, texts, language)!), Bold: true));
        if (record.Shares.Count > 0)
            blocks.Add(new PdfParagraph(texts.Get("CuProspectCoOwners", language, SharesText(record.Shares, language)!)));

        blocks.Add(new PdfHeading(texts.Get("CuProspectSectionStays", language)));
        blocks.Add(new PdfTable(
            [
                new PdfTableColumn(texts.Get("CuColProperty", language), 2),
                new PdfTableColumn(texts.Get("CuColCheckIn", language), 1.1),
                new PdfTableColumn(texts.Get("CuColCheckOut", language), 1.1),
                new PdfTableColumn(texts.Get("F24ExportColGrossBaseEuro", language), 1.2, PdfCellAlignment.Right),
                new PdfTableColumn(texts.Get("F24ExportColWithheldEuro", language), 1.2, PdfCellAlignment.Right),
            ],
            [
                .. record.Lines.Select<CuLine, IReadOnlyList<string>>(line =>
                [
                    PropertyText(line.PropertyId is { } id ? record.Properties.FirstOrDefault(p => p.Id == id) : null),
                    line.CheckIn is { } checkIn ? DocumentLanguages.Day(checkIn) : string.Empty,
                    line.CheckOut is { } checkOut ? DocumentLanguages.Day(checkOut) : string.Empty,
                    DocumentLanguages.Money(line.GrossBase, language),
                    DocumentLanguages.Money(line.Withheld, language),
                ]),
            ]));
        blocks.Add(new PdfParagraph(
            texts.Get("CuProspectTotals", language, DocumentLanguages.Money(record.GrossBase, language), DocumentLanguages.Money(record.Withheld, language)),
            Bold: true));
        blocks.Add(new PdfParagraph(texts.Get("CuProspectTaxCode", language, record.TaxCode ?? "-", string.Join(", ", record.RuleVersions))));

        return new PdfDocumentContent(texts.Get("CuProspectTitle", language, period), blocks)
        {
            Watermark = texts.Get("CuProspectWatermark", language),
            Language = DocumentLanguages.Normalize(language) == DocumentLanguages.English ? "en-GB" : "it-IT",
        };
    }

    /// <summary>The name of the file of the draft CU of an owner: <c>prospetto-cu-2026-3f2a….pdf</c> (the id of the owner, never its name).</summary>
    public static string CuProspectFileName(int year, Guid ownerId) =>
        string.Create(CultureInfo.InvariantCulture, $"prospetto-cu-{year:0000}-{ownerId:N}.pdf");

    // ---------------------------------------------------------------- the 770

    /// <summary>
    /// The table of the data of the 770: for each month and tax code the withholding made (and the base), the F24 it was paid with (its amount, due date, day paid, filing state and references)
    /// and the difference between what was withheld and what the F24 says. A month whose F24 was never generated says so, and the columns of the F24 stay empty (never a zero payment).
    /// </summary>
    public static ReportTable Tax770Table(Withholding770Data data, IDocumentTexts texts, string language)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(texts);

        var table = new ReportTable(
            texts.Get("Tax770ExportTitle", language, data.Year.ToString("0000", CultureInfo.InvariantCulture)),
            [
                texts.Get("F24DocColMonth", language),
                texts.Get("F24DocColYear", language),
                texts.Get("F24DocTaxCode", language),
                texts.Get("F24ExportColGrossBaseEuro", language),
                texts.Get("Tax770ColWithheld", language),
                texts.Get("Tax770ColF24Amount", language),
                texts.Get("AccPkgColDueDate", language),
                texts.Get("Tax770ColPaidOn", language),
                texts.Get("Tax770ColFilingState", language),
                texts.Get("Tax770ColHandoff", language),
                texts.Get("Tax770ColReceipt", language),
                texts.Get("Tax770ColDifference", language),
                texts.Get("AccPkgColEvidence", language),
            ]);
        foreach (var line in data.Lines)
        {
            table.Add(
                ReportCell.Integer(line.Month),
                ReportCell.Integer(data.Year),
                line.TaxCode,
                ReportCell.Money(line.GrossBase),
                ReportCell.Money(line.Withheld),
                ReportCell.Money(line.F24Amount),
                ReportCell.Date(line.F24DueDate),
                ReportCell.Date(line.F24PaidOn),
                line.FilingState is { } state ? texts.Get("TaxFilingState" + state, language) : texts.Get("Tax770NoF24", language),
                line.HandoffReference,
                line.ReceiptReference,
                ReportCell.Money(line.F24Amount is { } amount ? line.Withheld - amount : null),
                line.EvidenceCount);
        }

        return table;
    }

    /// <summary>The CSV of the data of the 770.</summary>
    public static byte[] Tax770Csv(Withholding770Data data, IDocumentTexts texts, string language) =>
        CsvWriter.Write(Tax770Table(data, texts, language), MaxRows);

    /// <summary>The Excel file of the data of the 770: one sheet, <see cref="Tax770SheetName"/>.</summary>
    public static byte[] Tax770Xlsx(Withholding770Data data, IDocumentTexts texts, string language) =>
        XlsxWriter.Write([new ReportSheet(Tax770SheetName, Tax770Table(data, texts, language))], MaxRows);

    /// <summary>The name of the file of the data of the 770 of a year: <c>770-2026.csv</c>.</summary>
    public static string Tax770FileName(int year, string extension) =>
        string.Create(CultureInfo.InvariantCulture, $"770-{year:0000}.{extension}");

    // ---------------------------------------------------------------- the communication of the short-let contracts

    /// <summary>
    /// The table of the communication of the contracts: for each contract the lessor (name, fiscal code, address), the property (address, comune, cadastral data and CIN when stored), the duration
    /// (the first and the last day, the nights), the consideration and why the agency did not withhold. Every row is a stay the payouts of the year decided without the agency's withholding.
    /// </summary>
    public static ReportTable ContractsTable(ContractsCommunication data, IDocumentTexts texts, string language)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(texts);

        var table = new ReportTable(
            texts.Get("ContractsExportTitle", language, data.Year.ToString("0000", CultureInfo.InvariantCulture)),
            [
                texts.Get("ContractsColLessor", language),
                texts.Get("CuColFiscalCode", language),
                texts.Get("CuColVatNumber", language),
                texts.Get("CuColStreet", language),
                texts.Get("CuColCivic", language),
                texts.Get("CuColPostalCode", language),
                texts.Get("CuColCity", language),
                texts.Get("CuColLegalAddress", language),
                texts.Get("CuColProperty", language),
                texts.Get("CuColPropertyAddress", language),
                texts.Get("CuColPropertyCity", language),
                texts.Get("CuColIstat", language),
                texts.Get("CuColCin", language),
                texts.Get("CuColCadastralSheet", language),
                texts.Get("CuColCadastralParcel", language),
                texts.Get("CuColCadastralSubaltern", language),
                texts.Get("CuColCadastralCategory", language),
                texts.Get("CuColCheckIn", language),
                texts.Get("CuColCheckOut", language),
                texts.Get("CuColNights", language),
                texts.Get("AccPkgColChannel", language),
                texts.Get("ContractsColCurrency", language),
                texts.Get("ContractsColConsideration", language),
                texts.Get("ContractsColReason", language),
                texts.Get("CuColMissing", language),
            ]);
        foreach (var row in data.Rows)
        {
            table.Add(
                row.Lessor.Name,
                row.Lessor.FiscalCode,
                row.Lessor.VatNumber,
                row.Lessor.Street,
                row.Lessor.Civic,
                row.Lessor.PostalCode,
                row.Lessor.City,
                row.Lessor.LegalAddress,
                row.Property?.Name,
                AddressOf(row.Property),
                CityOf(row.Property),
                row.Property?.ComuneIstatCode,
                row.Property?.Cin,
                row.Property?.CadastralSheet,
                row.Property?.CadastralParcel,
                row.Property?.CadastralSubaltern,
                row.Property?.CadastralCategory,
                ReportCell.Date(row.CheckIn),
                ReportCell.Date(row.CheckOut),
                ReportCell.Integer(row.Nights),
                row.Channel,
                row.Currency,
                ReportCell.Money(row.Consideration),
                ReasonText(row.Reason, texts, language),
                MissingText(row.Lessor.Missing, texts, language));
        }

        return table;
    }

    /// <summary>The CSV of the communication of the contracts.</summary>
    public static byte[] ContractsCsv(ContractsCommunication data, IDocumentTexts texts, string language) =>
        CsvWriter.Write(ContractsTable(data, texts, language), MaxRows);

    /// <summary>The Excel file of the communication of the contracts: one sheet.</summary>
    public static byte[] ContractsXlsx(ContractsCommunication data, IDocumentTexts texts, string language) =>
        XlsxWriter.Write([new ReportSheet(texts.Get("ContractsExportSheet", language), ContractsTable(data, texts, language))], MaxRows);

    /// <summary>The name of the file of the communication of the contracts of a year: <c>comunicazione-locazioni-brevi-2026.csv</c>.</summary>
    public static string ContractsFileName(int year, string extension) =>
        string.Create(CultureInfo.InvariantCulture, $"comunicazione-locazioni-brevi-{year:0000}.{extension}");

    // ---------------------------------------------------------------- the words

    private static string? EntityKindText(string? kind, IDocumentTexts texts, string language) => kind switch
    {
        nameof(OwnerEntityKind.NaturalPerson) => texts.Get("CuEntityKindNaturalPerson", language),
        nameof(OwnerEntityKind.LegalEntity) => texts.Get("CuEntityKindLegalEntity", language),
        _ => null,
    };

    private static string AttributionText(string attribution, IDocumentTexts texts, string language) => attribution switch
    {
        CuAttributions.Signatory => texts.Get("CuAttributionSignatory", language),
        CuAttributions.Share => texts.Get("CuAttributionShare", language),
        _ => texts.Get("CuAttributionOwner", language),
    };

    /// <summary>What each co-owner puts into a record: <c>Name: base / withholding</c>, separated by <c>;</c>; <c>null</c> for a record of one owner.</summary>
    private static string? SharesText(IReadOnlyList<CuShare> shares, string language) =>
        shares.Count == 0
            ? null
            : string.Join("; ", shares.Select(s => $"{s.Name}: {DocumentLanguages.Money(s.GrossBase, language)} / {DocumentLanguages.Money(s.Withheld, language)}"));

    private static string? MissingText(IReadOnlyList<string> missing, IDocumentTexts texts, string language) =>
        missing.Count == 0
            ? null
            : string.Join(", ", missing.Select(code => code switch
            {
                TaxReportingPeople.Missing.Name => texts.Get("CuMissingName", language),
                TaxReportingPeople.Missing.FiscalCode => texts.Get("CuMissingFiscalCode", language),
                TaxReportingPeople.Missing.Address => texts.Get("CuMissingAddress", language),
                _ => code,
            }));

    private static string ReasonText(string reason, IDocumentTexts texts, string language) => reason switch
    {
        OwnerWithholdingRules.AlreadyWithheldByPortal => texts.Get("ContractsReasonAlreadyWithheldByPortal", language),
        OwnerWithholdingRules.NoCollectionOrIntervention => texts.Get("ContractsReasonNoCollectionOrIntervention", language),
        OwnerWithholdingRules.RuleWithoutWithholding => texts.Get("ContractsReasonRuleWithoutWithholding", language),
        _ => reason,
    };

    /// <summary>The street address of a property, with its apartment when it has one (<c>Via Roma 1, int. 5</c>).</summary>
    private static string? AddressOf(CuProperty? property) =>
        property is null ? null : string.IsNullOrWhiteSpace(property.Unit) ? property.Address : $"{property.Address}, {property.Unit}";

    /// <summary>The postal code and the town of a property (<c>22100 Como</c>).</summary>
    private static string? CityOf(CuProperty? property) =>
        property is null ? null : $"{property.PostalCode} {property.City}".Trim();

    /// <summary>The property as a line of the PDF: its name and its address.</summary>
    private static string PropertyText(CuProperty? property) =>
        property is null ? string.Empty : $"{property.Name}, {AddressOf(property)}, {CityOf(property)}";

    /// <summary>The address of the recipient in one line: the structured address of the registry, else the legal address of the fiscal profile; <c>null</c> when there is none.</summary>
    private static string? AddressText(CuRecipient recipient)
    {
        var street = string.Join(" ", new[] { recipient.Street, recipient.Civic }.Where(part => !string.IsNullOrWhiteSpace(part)));
        var town = string.Join(" ", new[] { recipient.PostalCode, recipient.City, recipient.Province }.Where(part => !string.IsNullOrWhiteSpace(part)));
        var structured = string.Join(", ", new[] { street, town, recipient.Country }.Where(part => !string.IsNullOrWhiteSpace(part)));
        return structured.Length > 0 ? structured : string.IsNullOrWhiteSpace(recipient.LegalAddress) ? null : recipient.LegalAddress;
    }
}
