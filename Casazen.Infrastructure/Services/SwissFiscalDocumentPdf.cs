using System.Globalization;
using Casazen.Core.Documents;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;
using DocumentKind = Casazen.Core.Entities.Enums.FiscalDocumentKind;

namespace Casazen.Infrastructure.Services;

/// <summary>
/// The Swiss layout of a fiscal document (NF-87, NF-113, MWSTG art. 26, E11/F01/US01/T03), chosen by <see cref="FiscalDocumentPdfs"/> when the issuer of
/// the document is in Switzerland (its snapshot). It carries the data of art. 26: the issuer with its UID and the suffix «IVA» when it is registered, the
/// recipient, the date and the number, the period of the stay and the property, the service line by line with its VAT rate, the VAT per rate and the
/// total in CHF; for an issuer that is not registered, a receipt without VAT and the note «Non soggetto all'IVA».
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><b>Nothing Italian.</b> No courtesy copy of an electronic invoice, no nature code, no stamp duty, no SDI, no Italian fiscal code: none of them
/// exists for a Swiss document.</item>
/// <item><b>Stored, never recomputed.</b> The lines are the stored ones (net of VAT), the VAT per rate is the stored VAT of the lines
/// (<see cref="FiscalDocumentTotals.SummaryOf"/>), the totals are the document's.</item>
/// <item><b>Swiss figures.</b> Amounts with the it-CH culture and its apostrophe as the thousands separator (<c>1'234.50</c>), pinned here so every platform
/// writes the same (some write U+2019 instead).</item>
/// <item><b>Payment.</b> «Pagata tramite il portale» when the portal collected, «Pagata» when the money is collected, «Da incassare» otherwise: never the
/// name of a portal or of a supplier.</item>
/// <item><b>Language.</b> The labels in the language asked for (the guest's; Italian by default), from the same resource files as the Italian layout.</item>
/// </list>
/// </remarks>
public static class SwissFiscalDocumentPdf
{
    /// <summary>The figures of a Swiss document: the it-CH culture with the apostrophe as the thousands separator and the point as the decimal one.</summary>
    private static readonly NumberFormatInfo SwissNumbers = CreateSwissNumbers();

    /// <summary>What a line outside the VAT shows in the column of the rate.</summary>
    private const string OutsideVat = "—";

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

        var registered = issuer.VatRegistered && document.Kind != DocumentKind.Receipt;
        var currency = document.Currency;
        var kind = FiscalDocumentPdfs.KindLabel(document.Kind, texts, language);
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
        blocks.Add(new PdfParagraph(string.Join('\n', RecipientLines(document))));

        var stay = StayLines(document, texts, language, propertyName).ToList();
        if (stay.Count > 0)
            blocks.Add(new PdfParagraph(string.Join('\n', stay)));

        var lines = document.Lines.OrderBy(l => l.LineNumber).ToList();
        var columns = new List<PdfTableColumn>
        {
            new(texts.Get("FiscalDocPdfColDescription", language), 2.8),
            new(texts.Get("FiscalDocPdfColQuantity", language), 0.8, PdfCellAlignment.Right),
            new(texts.Get("FiscalDocPdfColUnitPrice", language, currency), 1.2, PdfCellAlignment.Right),
            new(texts.Get("FiscalDocPdfColTotal", language, currency), 1.2, PdfCellAlignment.Right),
        };
        if (registered)
            columns.Add(new PdfTableColumn(texts.Get("FiscalDocPdfChColRate", language), 0.8, PdfCellAlignment.Right));

        blocks.Add(new PdfHeading(texts.Get("FiscalDocPdfLinesHeading", language)));
        blocks.Add(new PdfTable(
            columns,
            lines.Select(l =>
                {
                    var cells = new List<string>
                    {
                        Description(l, texts, language),
                        l.Quantity.ToString("0.##", SwissNumbers),
                        Money(l.UnitPrice),
                        Money(l.TotalPrice),
                    };
                    if (registered)
                        cells.Add(l.VatRatePercent > 0m ? Rate(l.VatRatePercent) : OutsideVat);
                    return (IReadOnlyList<string>)cells;
                })
                .ToList()));

        var totals = new List<IReadOnlyList<string>>();
        if (registered)
        {
            // The VAT of each rate: the base, the rate and the VAT, the stored VAT of the lines of that rate.
            var summary = VatSummaryOf(document);
            if (summary.Count > 0)
            {
                blocks.Add(new PdfHeading(texts.Get("FiscalDocPdfSummaryHeading", language), 3));
                blocks.Add(new PdfTable(
                    [
                        new PdfTableColumn(texts.Get("FiscalDocPdfChColRate", language), 1.0),
                        new PdfTableColumn(texts.Get("FiscalDocPdfColTaxable", language, currency), 1.2, PdfCellAlignment.Right),
                        new PdfTableColumn(texts.Get("FiscalDocPdfColTax", language, currency), 1.2, PdfCellAlignment.Right),
                    ],
                    summary.Select(s => (IReadOnlyList<string>)new[] { Rate(s.VatRatePercent), Money(s.TaxableAmount), Money(s.Tax) }).ToList()));
            }

            totals.Add([texts.Get("FiscalDocPdfTaxableBase", language), Money(document.TaxableBase)]);
            totals.Add([texts.Get("FiscalDocPdfVatAmount", language), Money(document.VatAmount)]);
        }

        totals.Add([texts.Get("FiscalDocPdfTotal", language), Money(document.Total)]);
        blocks.Add(new PdfTable(
            [
                new PdfTableColumn(texts.Get("FiscalDocPdfColItem", language), 2.4),
                new PdfTableColumn(texts.Get("FiscalDocPdfColAmount", language, currency), 1.2, PdfCellAlignment.Right),
            ],
            totals));

        if (!registered)
            blocks.Add(new PdfParagraph(texts.Get("FiscalDocPdfChNotVatRegistered", language)));
        if (document.Kind != DocumentKind.CreditNote)
            blocks.Add(new PdfParagraph(PaymentLabel(document, texts, language), Bold: true));

        return new PdfDocumentContent(title, blocks)
        {
            Language = DocumentLanguages.Normalize(language) == DocumentLanguages.English ? "en-GB" : "it-CH",
            Watermark = document.Status == FiscalDocumentStatus.Draft ? texts.Get("FiscalDocPdfDraftWatermark", language) : null,
        };
    }

    /// <summary>An amount of a Swiss document: two decimals, the apostrophe between the thousands (<c>1'234.50</c>).</summary>
    public static string Money(decimal amount) => amount.ToString("N2", SwissNumbers);

    /// <summary>A VAT rate of a Swiss document: <c>3.8%</c>, <c>8.1%</c>.</summary>
    public static string Rate(decimal percent) => percent.ToString("0.##", SwissNumbers) + "%";

    /// <summary>The rates of the document with their base and their stored VAT; the lines outside the VAT (the tourist tax) are not a rate.</summary>
    internal static IReadOnlyList<FiscalDocumentVatSummary> VatSummaryOf(FiscalDocument document) =>
        FiscalDocumentTotals.SummaryOf(FiscalDocumentService.AmountsOf(document), document.VatAmount)
            .Where(s => s.VatRatePercent > 0m)
            .ToList();

    private static IEnumerable<string> IssuerLines(FiscalIssuerSnapshot issuer, IDocumentTexts texts, string language)
    {
        yield return issuer.DisplayName;
        var street = string.Join(' ', new[] { issuer.AddressStreet, issuer.AddressCivic }.Where(s => !string.IsNullOrWhiteSpace(s)));
        if (street.Length > 0)
            yield return street;
        var city = string.Join(' ', new[] { issuer.PostalCode, issuer.City }.Where(s => !string.IsNullOrWhiteSpace(s)));
        if (city.Length > 0)
            yield return $"{city} {issuer.CountryCode}".Trim();

        // MWSTG art. 26: the UID followed by the suffix of the VAT register in the language of the document.
        if (issuer.VatRegistered && !string.IsNullOrWhiteSpace(issuer.SwissVatNumber))
            yield return texts.Get("FiscalDocPdfChVatNumber", language, issuer.SwissVatNumber);
    }

    /// <summary>The recipient: the name, then the address when it has one. No Italian fiscal code or VAT number.</summary>
    private static IEnumerable<string> RecipientLines(FiscalDocument document)
    {
        if (!string.IsNullOrWhiteSpace(document.RecipientName))
            yield return document.RecipientName;
        var street = string.Join(' ', new[] { document.RecipientAddressStreet, document.RecipientAddressCivic }.Where(s => !string.IsNullOrWhiteSpace(s)));
        if (street.Length > 0)
            yield return street;
        var city = string.Join(' ', new[] { document.RecipientPostalCode, document.RecipientCity, document.RecipientCountryCode }.Where(s => !string.IsNullOrWhiteSpace(s)));
        if (city.Length > 0)
            yield return city;
    }

    /// <summary>The period of the stay (art. 26: the date or the period of the service) and the property, when the document has them.</summary>
    private static IEnumerable<string> StayLines(FiscalDocument document, IDocumentTexts texts, string language, string? propertyName)
    {
        if (document.ServicePeriodFrom is { } from && document.ServicePeriodTo is { } to)
            yield return texts.Get("FiscalDocPdfChStayPeriod", language, DocumentLanguages.Day(from), DocumentLanguages.Day(to));
        if (!string.IsNullOrWhiteSpace(propertyName))
            yield return texts.Get("FiscalDocPdfChProperty", language, propertyName.Trim());
    }

    /// <summary>The description of a line; the tourist tax is always labelled as the tax outside the VAT, in the language of the document.</summary>
    private static string Description(FiscalDocumentLine line, IDocumentTexts texts, string language) =>
        line.LineType == FiscalLineType.TouristTax ? texts.Get("FiscalDocLineTouristTaxCh", language) : line.Description;

    /// <summary>The payment state from who collected: the portal, the money collected, or still to collect. Never the name of a portal.</summary>
    private static string PaymentLabel(FiscalDocument document, IDocumentTexts texts, string language) =>
        document.PaymentChannel == FiscalPaymentChannel.Portal ? texts.Get("FiscalDocPdfChPaidPortal", language)
        : document.PaymentState == FiscalDocumentPaymentState.Paid ? texts.Get("FiscalDocPdfChPaid", language)
        : texts.Get("FiscalDocPdfChToCollect", language);

    private static NumberFormatInfo CreateSwissNumbers()
    {
        var numbers = (NumberFormatInfo)CultureInfo.GetCultureInfo("it-CH").NumberFormat.Clone();
        numbers.NumberGroupSeparator = "'";
        numbers.NumberDecimalSeparator = ".";
        numbers.NumberGroupSizes = [3];
        return NumberFormatInfo.ReadOnly(numbers);
    }
}
