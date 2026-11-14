using System.Text;
using Casazen.Core.Documents;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;
using Casazen.Infrastructure.Documents;
using Casazen.Infrastructure.Services;
using Casazen.Tests.Unit.Documents;
using Casazen.Web.Resources;
using Xunit;
using DocumentKind = Casazen.Core.Entities.Enums.FiscalDocumentKind;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// E11/F01/US01/T03 (NF-87, NF-113, MWSTG art. 26): the Swiss layout of a fiscal document. Tested on the model that feeds the renderer (the blocks of the
/// PDF), and once on the rendered file: the UID of the issuer with the suffix of the VAT register, the VAT of each rate with its base, the total in CHF
/// written the Swiss way, the receipt of an issuer that is not registered without VAT, and nothing Italian.
/// </summary>
public class SwissFiscalDocumentPdfTests
{
    private const string Uid = "CHE-109.322.551";

    /// <summary>
    /// PROOF TEST: data rule. The invoice of a registered Swiss issuer (4 nights CHF 600, cleaning 80 with the stay, an extra 40, the tourist tax 16) shows
    /// «N. IVA CHE-109.322.551 IVA», the two rates of the summary with their bases and VAT (3.8%: 655.11 and 24.89; 8.1%: 37.00 and 3.00), the total
    /// CHF 736.00, the stay and the property, the tourist tax labelled outside the VAT, and the payment through the portal without naming it.
    /// </summary>
    [Fact]
    public void SwissInvoicePdf_HasVatNumberAndRateSummary_Test()
    {
        var document = SwissInvoice();

        var content = FiscalDocumentPdfs.Pdf(document, Issuer(registered: true), new ResxDocumentTexts(), "it", propertyName: "TEST Casa sul Lago");
        var text = TextOf(content);

        Assert.Equal("Fattura n. 2026/1", content.Title);
        Assert.Equal("it-CH", content.Language);
        Assert.Contains($"N. IVA {Uid} IVA", text, StringComparison.Ordinal);
        Assert.Contains("TEST Quokka Sagl", text, StringComparison.Ordinal);
        Assert.Contains("6900 Lugano CH", text, StringComparison.Ordinal);
        Assert.Contains("Soggiorno dal 14/11/2026 al 18/11/2026", text, StringComparison.Ordinal);
        Assert.Contains("Alloggio: TEST Casa sul Lago", text, StringComparison.Ordinal);

        // The VAT summary: one row per rate, its base and its VAT (the stored VAT of the lines); the tourist tax is not a rate.
        var summary = Assert.Single(content.Blocks.OfType<PdfTable>(), t => t.Columns[0].Header == "Aliquota IVA" && t.Columns.Count == 3);
        Assert.Equal(
            new[] { new[] { "8.1%", "37.00", "3.00" }, new[] { "3.8%", "655.11", "24.89" } },
            summary.Rows.Select(r => r.ToArray()).ToArray());

        // The lines with their VAT rate; the tourist tax labelled as the tax outside the VAT.
        var lines = Assert.Single(content.Blocks.OfType<PdfTable>(), t => t.Columns.Count == 5);
        Assert.Equal(new[] { "3.8%", "3.8%", "8.1%", "—" }, lines.Rows.Select(r => r[4]).ToArray());
        Assert.Equal("Tassa di soggiorno (non soggetta all'IVA)", lines.Rows[3][0]);

        // The totals in CHF, written the Swiss way.
        var totals = Assert.Single(content.Blocks.OfType<PdfTable>(), t => t.Columns.Count == 2);
        Assert.Equal("Importo (CHF)", totals.Columns[1].Header);
        Assert.Equal(new[] { "708.11", "27.89", "736.00" }, totals.Rows.Select(r => r[1]).ToArray());
        Assert.Equal("1'234.50", SwissFiscalDocumentPdf.Money(1234.5m));
        Assert.Equal("12'345'678.90", SwissFiscalDocumentPdf.Money(12345678.9m));

        Assert.Contains("Pagata tramite il portale", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Booking", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Octorate", text, StringComparison.OrdinalIgnoreCase);
        AssertNothingItalian(text);
    }

    /// <summary>
    /// The receipt of a Swiss issuer that is not registered: no VAT column, no VAT summary, no VAT number, the note «Non soggetto all'IVA», the total in CHF;
    /// the payment state from the payments when no portal collected.
    /// </summary>
    [Fact]
    public void SwissReceiptPdf_OfAnIssuerNotRegistered_ShowsNoVatAndTheNote()
    {
        var document = SwissInvoice();
        document.Kind = DocumentKind.Receipt;
        document.PaymentChannel = FiscalPaymentChannel.Unknown;
        document.PaymentState = FiscalDocumentPaymentState.ToCollect;
        foreach (var line in document.Lines)
        {
            line.TotalPrice += line.VatAmount ?? 0m;
            line.UnitPrice = line.TotalPrice;
            line.VatRatePercent = 0m;
            line.VatAmount = 0m;
        }

        document.TaxableBase = 736.00m;
        document.VatAmount = 0m;

        var content = FiscalDocumentPdfs.Pdf(document, Issuer(registered: false), new ResxDocumentTexts(), "it");
        var text = TextOf(content);

        Assert.Equal("Ricevuta n. 2026/1", content.Title);
        Assert.Contains("Non soggetto all'IVA", text, StringComparison.Ordinal);
        Assert.DoesNotContain("N. IVA", text, StringComparison.Ordinal);
        Assert.DoesNotContain(content.Blocks.OfType<PdfTable>(), t => t.Columns.Any(c => c.Header == "Aliquota IVA"));
        Assert.DoesNotContain("Riepilogo IVA", text, StringComparison.Ordinal);
        var totals = Assert.Single(content.Blocks.OfType<PdfTable>(), t => t.Columns.Count == 2);
        Assert.Equal(new[] { "736.00" }, totals.Rows.Select(r => r[1]).ToArray());
        Assert.Contains("Da incassare", text, StringComparison.Ordinal);
        AssertNothingItalian(text);
    }

    /// <summary>
    /// The same invoice in English (the language of the guest), rendered by the real renderer: a real PDF, «VAT no. CHE-109.322.551 VAT», «Invoice», the
    /// Swiss figures with their apostrophe and nothing Italian; a draft carries the watermark.
    /// </summary>
    [Fact]
    public void SwissInvoicePdf_InEnglish_IsARealPdf_AndADraftCarriesTheWatermark()
    {
        var document = SwissInvoice();
        document.Lines.Add(new FiscalDocumentLine
        {
            OrgId = document.OrgId,
            DocumentId = document.Id,
            LineNumber = 5,
            LineType = FiscalLineType.ExtraService,
            Description = "TEST Escursione in barca",
            Quantity = 1m,
            UnitPrice = 1156.06m,
            TotalPrice = 1156.06m,
            VatRatePercent = 8.1m,
            VatAmount = 93.64m,
        });

        var content = FiscalDocumentPdfs.Pdf(document, Issuer(registered: true), new ResxDocumentTexts(), "en", propertyName: "TEST Casa sul Lago");
        var pdf = new MigraDocPdfDocumentRenderer().Render(content);
        var text = PdfTestReader.Text(pdf);

        Assert.StartsWith("%PDF", Encoding.ASCII.GetString(pdf, 0, 4));
        Assert.Equal("Invoice no. 2026/1", content.Title);
        Assert.Contains($"VAT no. {Uid} VAT", TextOf(content), StringComparison.Ordinal);
        Assert.Contains("Tourist tax (not subject to VAT)", TextOf(content), StringComparison.Ordinal);
        Assert.Contains("1'156.06", text, StringComparison.Ordinal);
        Assert.Contains("Paid through the portal", text, StringComparison.Ordinal);
        AssertNothingItalian(TextOf(content));

        document.Status = FiscalDocumentStatus.Draft;
        document.Number = null;
        var draft = FiscalDocumentPdfs.Pdf(document, Issuer(registered: true), new ResxDocumentTexts(), "en");
        Assert.Equal("DRAFT", draft.Watermark);
        Assert.Equal("Invoice (draft)", draft.Title);
    }

    /// <summary>The words of an Italian document never appear on a Swiss one: courtesy copy, SDI, nature codes, stamp duty, Italian tax ids.</summary>
    private static void AssertNothingItalian(string text)
    {
        foreach (var italian in new[]
                 {
                     "Copia di cortesia", "Courtesy copy", "Sistema di Interscambio", "Exchange System", "SDI", "Imposta di bollo", "Stamp duty", "bollo",
                     "Partita IVA", "VAT number:", "Codice fiscale", "Tax code", "N1", "N2.2", "N4", "natura", "nature",
                 })
        {
            Assert.DoesNotContain(italian, text, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// An issued Swiss invoice made the way the service makes it: the stored lines and VAT of 4 nights CHF 600, cleaning 80, an extra 40 and the tourist tax
    /// 16 (<see cref="FiscalDocumentTotals.FromGross"/> at 3.8% and 8.1%), paid through the portal.
    /// </summary>
    private static FiscalDocument SwissInvoice()
    {
        var amounts = FiscalDocumentTotals.FromGross(
            [
                new FiscalDocumentGrossLine("Soggiorno di 4 notti", 1m, 600m, 3.8m, null, FiscalLineType.Accommodation),
                new FiscalDocumentGrossLine("Pulizie finali", 1m, 80m, 3.8m, null, FiscalLineType.Cleaning),
                new FiscalDocumentGrossLine("TEST Servizio extra", 1m, 40m, 8.1m, null, FiscalLineType.ExtraService),
                new FiscalDocumentGrossLine("Tassa di soggiorno (non soggetta all'IVA)", 1m, 16m, 0m, null, FiscalLineType.TouristTax),
            ],
            null);
        var document = new FiscalDocument
        {
            OrgId = Guid.NewGuid(),
            Kind = DocumentKind.Invoice,
            Status = FiscalDocumentStatus.Issued,
            Series = "2026",
            SequenceNumber = 1,
            Number = "2026/1",
            IssueDate = new DateOnly(2026, 11, 19),
            Currency = "CHF",
            RecipientName = "TEST Ospite Svizzero",
            RecipientFiscalCode = "RSSMRA80A01H501U",
            ServicePeriodFrom = new DateOnly(2026, 11, 14),
            ServicePeriodTo = new DateOnly(2026, 11, 18),
            TaxableBase = amounts.TaxableBase,
            VatAmount = amounts.VatAmount,
            StampDuty = amounts.StampDuty,
            Total = amounts.Total,
            PaymentState = FiscalDocumentPaymentState.ToCollect,
            PaymentChannel = FiscalPaymentChannel.Portal,
            SdiStatus = FiscalDocumentSdiStatuses.NotApplicable,
        };
        foreach (var line in amounts.Lines)
        {
            document.Lines.Add(new FiscalDocumentLine
            {
                OrgId = document.OrgId,
                DocumentId = document.Id,
                LineNumber = line.LineNumber,
                LineType = line.LineType,
                Description = line.Description,
                Quantity = line.Quantity,
                UnitPrice = line.UnitPrice,
                TotalPrice = line.TotalPrice,
                VatRatePercent = line.VatRatePercent,
                VatNatureCode = line.NatureCode,
                VatAmount = line.VatAmount,
            });
        }

        return document;
    }

    private static FiscalIssuerSnapshot Issuer(bool registered) => new()
    {
        LegalName = "TEST Quokka Sagl",
        AddressStreet = "Via Nassa",
        AddressCivic = "5",
        PostalCode = "6900",
        City = "Lugano",
        CountryCode = "CH",
        VatRegistered = registered,
        SwissVatNumber = registered ? Uid : null,
        Regime = registered ? DocumentRegime.SwissVatRegistered : DocumentRegime.SwissNotRegistered,
        IbanMasked = "CH93…2957",
    };

    private static string TextOf(PdfDocumentContent content) =>
        string.Join('\n', new[] { content.Title }.Concat(content.Blocks.SelectMany(TextsOf)));

    private static IEnumerable<string> TextsOf(PdfBlock block) => block switch
    {
        PdfHeading heading => new[] { heading.Text },
        PdfParagraph paragraph => new[] { paragraph.Text },
        PdfTable table => table.Columns.Select(c => c.Header).Concat(table.Rows.SelectMany(r => r)),
        _ => Array.Empty<string>(),
    };
}
