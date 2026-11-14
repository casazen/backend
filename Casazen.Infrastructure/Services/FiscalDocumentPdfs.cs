using System.Globalization;
using Casazen.Core.Documents;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;
using DocumentKind = Casazen.Core.Entities.Enums.FiscalDocumentKind;

namespace Casazen.Infrastructure.Services;

/// <summary>
/// The PDF of a fiscal document CasaZen issues (FIS-03, FIS-07, NF-83, NF-87, ADR-019-R10, E06/F01/US02/T04, E11/F01/US01/T01), in the style of
/// <see cref="FiscalReportDocuments"/>: A4, laid out by <see cref="IPdfDocumentRenderer"/> (the header of the lines table repeated on every page), labels
/// from the resource files in Italian or English. It is never used for an owner statement, which has its own non-fiscal layout.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><b>The issuer as it was.</b> The issuer is the snapshot of the document (<see cref="FiscalIssuerSnapshot"/>): for an issued document the one kept
/// at the issue, for a draft the profile of today. A Swiss issuer gets the Swiss layout (<see cref="SwissFiscalDocumentPdf"/>).</item>
/// <item><b>Stored, never recomputed.</b> The lines, the taxable base, the VAT, the stamp duty and the total are printed as the document stores them; the
/// VAT summary adds up the stored lines of each rate and prints the stored VAT of the document against its one rate (a document with several rates prints
/// its VAT in the total only).</item>
/// <item><b>Currency.</b> The currency of the document, named in the headers: this code never writes one.</item>
/// <item><b>Courtesy copy.</b> A document with FatturaPA XML says it is the courtesy copy of the electronic invoice; a draft carries the watermark of a
/// draft.</item>
/// </list>
/// </remarks>
public static class FiscalDocumentPdfs
{
    /// <summary>The PDF of <paramref name="document"/> with the issuer as <paramref name="issuer"/> and <paramref name="org"/> say now (a draft).</summary>
    public static PdfDocumentContent Pdf(
        FiscalDocument document,
        OrgFiscalIssuerProfile? issuer,
        Org org,
        IDocumentTexts texts,
        string language,
        FiscalDocument? credited = null) =>
        Pdf(document, FiscalIssuerSnapshot.From(issuer, org), texts, language, credited);

    /// <summary>
    /// The PDF of <paramref name="document"/> issued by <paramref name="issuer"/> (the snapshot of the document, or the profile of today for a draft);
    /// <paramref name="propertyName"/> is the name of the property of the stay, when there is one.
    /// </summary>
    public static PdfDocumentContent Pdf(
        FiscalDocument document,
        FiscalIssuerSnapshot issuer,
        IDocumentTexts texts,
        string language,
        FiscalDocument? credited = null,
        string? propertyName = null)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(issuer);
        ArgumentNullException.ThrowIfNull(texts);

        var currency = document.Currency;
        var kind = KindLabel(document.Kind, texts, language);
        var title = document.Number is { Length: > 0 } number
            ? texts.Get("FiscalDocPdfTitle", language, kind, number)
            : texts.Get("FiscalDocPdfTitleDraft", language, kind);

        var blocks = new List<PdfBlock>();
        if (document.IssueDate is { } issued)
            blocks.Add(new PdfParagraph(texts.Get("FiscalDocPdfIssueDate", language, DocumentLanguages.Day(issued))));
        if (credited is not null)
        {
            blocks.Add(new PdfParagraph(texts.Get(
                "FiscalDocPdfCredits",
                language,
                credited.Number ?? string.Empty,
                credited.IssueDate is { } day ? DocumentLanguages.Day(day) : string.Empty)));
        }

        blocks.Add(new PdfHeading(texts.Get("FiscalDocPdfIssuer", language), 3));
        blocks.Add(new PdfParagraph(string.Join('\n', IssuerLines(issuer, texts, language))));
        blocks.Add(new PdfHeading(texts.Get("FiscalDocPdfRecipient", language), 3));
        blocks.Add(new PdfParagraph(string.Join('\n', RecipientLines(document, texts, language))));

        var lines = document.Lines.OrderBy(l => l.LineNumber).ToList();
        blocks.Add(new PdfHeading(texts.Get("FiscalDocPdfLinesHeading", language)));
        blocks.Add(new PdfTable(
            [
                new PdfTableColumn(texts.Get("FiscalDocPdfColLine", language), 0.5, PdfCellAlignment.Right),
                new PdfTableColumn(texts.Get("FiscalDocPdfColDescription", language), 2.6),
                new PdfTableColumn(texts.Get("FiscalDocPdfColQuantity", language), 0.8, PdfCellAlignment.Right),
                new PdfTableColumn(texts.Get("FiscalDocPdfColUnitPrice", language, currency), 1.2, PdfCellAlignment.Right),
                new PdfTableColumn(texts.Get("FiscalDocPdfColTotal", language, currency), 1.2, PdfCellAlignment.Right),
                new PdfTableColumn(texts.Get("FiscalDocPdfColVat", language), 0.8, PdfCellAlignment.Right),
            ],
            lines.Select(l => Row(
                    l.LineNumber.ToString(CultureInfo.InvariantCulture),
                    l.Description,
                    l.Quantity.ToString("0.##", DocumentLanguages.CultureOf(language)),
                    DocumentLanguages.Money(l.UnitPrice, language),
                    DocumentLanguages.Money(l.TotalPrice, language),
                    RateLabel(l.VatRatePercent, l.VatNatureCode, language)))
                .ToList()));

        var groups = lines
            .GroupBy(l => (l.VatRatePercent, Nature: l.VatRatePercent == 0m ? l.VatNatureCode : null))
            .OrderByDescending(g => g.Key.VatRatePercent)
            .ToList();
        var taxedGroups = groups.Count(g => g.Key.VatRatePercent > 0m);
        if (groups.Count > 0)
        {
            blocks.Add(new PdfHeading(texts.Get("FiscalDocPdfSummaryHeading", language), 3));
            blocks.Add(new PdfTable(
                [
                    new PdfTableColumn(texts.Get("FiscalDocPdfColRate", language), 1.4),
                    new PdfTableColumn(texts.Get("FiscalDocPdfColTaxable", language, currency), 1.2, PdfCellAlignment.Right),
                    new PdfTableColumn(texts.Get("FiscalDocPdfColTax", language, currency), 1.2, PdfCellAlignment.Right),
                ],
                groups.Select(g => Row(
                        RateLabel(g.Key.VatRatePercent, g.Key.Nature, language),
                        DocumentLanguages.Money(g.Sum(l => l.TotalPrice), language),
                        g.Key.VatRatePercent == 0m
                            ? DocumentLanguages.Money(0m, language)
                            : taxedGroups == 1 ? DocumentLanguages.Money(document.VatAmount, language) : "—"))
                    .ToList()));
        }

        var totals = new List<IReadOnlyList<string>>
        {
            Row(texts.Get("FiscalDocPdfTaxableBase", language), DocumentLanguages.Money(document.TaxableBase, language)),
            Row(texts.Get("FiscalDocPdfVatAmount", language), DocumentLanguages.Money(document.VatAmount, language)),
        };
        if (document.StampDuty > 0m)
            totals.Add(Row(texts.Get("FiscalDocPdfStampDuty", language), DocumentLanguages.Money(document.StampDuty, language)));
        totals.Add(Row(texts.Get("FiscalDocPdfTotal", language), DocumentLanguages.Money(document.Total, language)));
        blocks.Add(new PdfTable(
            [
                new PdfTableColumn(texts.Get("FiscalDocPdfColItem", language), 2.4),
                new PdfTableColumn(texts.Get("FiscalDocPdfColAmount", language, currency), 1.2, PdfCellAlignment.Right),
            ],
            totals));

        blocks.Add(new PdfParagraph(texts.Get("FiscalDocPdfPayment", language, PaymentLabel(document.PaymentState, texts, language))));
        if (!string.IsNullOrEmpty(document.FatturaPaXml))
            blocks.Add(new PdfParagraph(texts.Get("FiscalDocPdfCourtesyCopy", language)));

        return new PdfDocumentContent(title, blocks)
        {
            Language = DocumentLanguages.Normalize(language) == DocumentLanguages.English ? "en-GB" : "it-IT",
            Watermark = document.Status == FiscalDocumentStatus.Draft ? texts.Get("FiscalDocPdfDraftWatermark", language) : null,
        };
    }

    private static IEnumerable<string> IssuerLines(FiscalIssuerSnapshot issuer, IDocumentTexts texts, string language)
    {
        yield return issuer.DisplayName;
        var street = string.Join(' ', new[] { issuer.AddressStreet, issuer.AddressCivic }.Where(s => !string.IsNullOrWhiteSpace(s)));
        if (street.Length > 0)
            yield return street;
        var city = string.Join(' ', new[] { issuer.PostalCode, issuer.City, issuer.Province is { Length: > 0 } p ? $"({p})" : null }
            .Where(s => !string.IsNullOrWhiteSpace(s)));
        if (city.Length > 0)
            yield return $"{city} {issuer.CountryCode}".Trim();

        if (!string.IsNullOrWhiteSpace(issuer.VatNumber))
            yield return texts.Get("FiscalDocPdfVatNumber", language, issuer.VatNumber);
        if (!string.IsNullOrWhiteSpace(issuer.FiscalCode))
            yield return texts.Get("FiscalDocPdfFiscalCode", language, issuer.FiscalCode);
    }

    private static IEnumerable<string> RecipientLines(FiscalDocument document, IDocumentTexts texts, string language)
    {
        if (!string.IsNullOrWhiteSpace(document.RecipientName))
            yield return document.RecipientName;
        var street = string.Join(' ', new[] { document.RecipientAddressStreet, document.RecipientAddressCivic }.Where(s => !string.IsNullOrWhiteSpace(s)));
        if (street.Length > 0)
            yield return street;
        var city = string.Join(' ', new[]
            {
                document.RecipientPostalCode,
                document.RecipientCity,
                document.RecipientProvince is { Length: > 0 } p ? $"({p})" : null,
                document.RecipientCountryCode,
            }.Where(s => !string.IsNullOrWhiteSpace(s)));
        if (city.Length > 0)
            yield return city;
        if (!string.IsNullOrWhiteSpace(document.RecipientVatNumber))
            yield return texts.Get("FiscalDocPdfVatNumber", language, document.RecipientVatNumber);
        if (!string.IsNullOrWhiteSpace(document.RecipientFiscalCode))
            yield return texts.Get("FiscalDocPdfFiscalCode", language, document.RecipientFiscalCode);
    }

    /// <summary>The word of the kind of a document (invoice, receipt, credit note, proforma): also the one of the list exported to CSV and Excel.</summary>
    internal static string KindLabel(DocumentKind kind, IDocumentTexts texts, string language) => kind switch
    {
        DocumentKind.Receipt => texts.Get("FiscalDocPdfKindReceipt", language),
        DocumentKind.CreditNote => texts.Get("FiscalDocPdfKindCreditNote", language),
        DocumentKind.Proforma => texts.Get("FiscalDocPdfKindProforma", language),
        _ => texts.Get("FiscalDocPdfKindInvoice", language),
    };

    private static string PaymentLabel(FiscalDocumentPaymentState state, IDocumentTexts texts, string language) => state switch
    {
        FiscalDocumentPaymentState.Paid => texts.Get("FiscalDocPdfPaymentPaid", language),
        FiscalDocumentPaymentState.ToCollect => texts.Get("FiscalDocPdfPaymentToCollect", language),
        _ => texts.Get("FiscalDocPdfPaymentUnknown", language),
    };

    private static IReadOnlyList<string> Row(params string[] cells) => cells;

    /// <summary>The VAT of a line or a summary row: the rate in percent, or the nature code of an operation without VAT.</summary>
    private static string RateLabel(decimal rate, string? nature, string language) =>
        rate == 0m && !string.IsNullOrWhiteSpace(nature)
            ? nature
            : rate.ToString("0.##", DocumentLanguages.CultureOf(language)) + "%";
}
