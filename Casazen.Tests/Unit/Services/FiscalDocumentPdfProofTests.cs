using System.Text;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Infrastructure.Documents;
using Casazen.Infrastructure.Services;
using Casazen.Tests.Unit.Documents;
using Casazen.Web.Resources;
using Xunit;
using DocumentKind = Casazen.Core.Entities.Enums.FiscalDocumentKind;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// E06/F01/US02/T04 (NF-83, NF-87): the PDF of a guest or direct-booking fiscal document is a real PDF that prints the stored lines, taxable base,
/// VAT, stamp duty and total unchanged (money rule), in the currency of the document, with Italian and English labels.
/// </summary>
public class FiscalDocumentPdfProofTests
{
    [Fact]
    public void FiscalDocumentPdf_IsRealPdf_AndTotalsMatchDocument_Test()
    {
        var document = Issued("CHF");

        var pdf = new MigraDocPdfDocumentRenderer().Render(FiscalDocumentPdfs.Pdf(document, Issuer(), Org(), new ResxDocumentTexts(), "it"));
        var text = PdfTestReader.Text(pdf);

        Assert.StartsWith("%PDF", Encoding.ASCII.GetString(pdf, 0, 4));
        Assert.Contains("Fattura n. 2026/7", text, StringComparison.Ordinal);
        Assert.Contains("330,18", text, StringComparison.Ordinal);
        Assert.Contains("31,82", text, StringComparison.Ordinal);
        Assert.Contains("2,00", text, StringComparison.Ordinal);
        Assert.Contains("362,00", text, StringComparison.Ordinal);
        Assert.Contains("272,73", text, StringComparison.Ordinal);
        Assert.Contains("(CHF)", text, StringComparison.Ordinal);
        Assert.DoesNotContain("EUR", text, StringComparison.Ordinal);
        Assert.Contains("Copia di cortesia", text, StringComparison.Ordinal);
    }

    [Fact]
    public void FiscalDocumentPdf_EnglishLabels_AndADraftCarriesTheWatermark()
    {
        var draft = Issued("EUR");
        draft.Status = FiscalDocumentStatus.Draft;
        draft.Number = null;
        draft.SequenceNumber = null;
        draft.FatturaPaXml = null;

        var content = FiscalDocumentPdfs.Pdf(draft, null, Org(), new ResxDocumentTexts(), "en");
        var text = PdfTestReader.Text(new MigraDocPdfDocumentRenderer().Render(content));

        Assert.Equal("DRAFT", content.Watermark);
        Assert.Equal("Invoice (draft)", content.Title);
        Assert.Contains("Document total", text, StringComparison.Ordinal);
        Assert.Contains("362.00", text, StringComparison.Ordinal);
    }

    private static FiscalDocument Issued(string currency)
    {
        var document = new FiscalDocument
        {
            OrgId = Guid.NewGuid(),
            Kind = DocumentKind.Invoice,
            Status = FiscalDocumentStatus.Issued,
            Series = "2026",
            SequenceNumber = 7,
            Number = "2026/7",
            IssueDate = new DateOnly(2026, 11, 12),
            Currency = currency,
            RecipientName = "TEST Ospite Srl",
            RecipientVatNumber = "IT09876543217",
            RecipientAddressStreet = "Via del Cliente",
            RecipientPostalCode = "00100",
            RecipientCity = "Roma",
            RecipientCountryCode = "IT",
            TaxableBase = 330.18m,
            VatAmount = 31.82m,
            StampDuty = 2.00m,
            Total = 362.00m,
            PaymentState = FiscalDocumentPaymentState.Paid,
            FatturaPaXml = "<xml/>",
        };
        document.Lines.Add(Line(document, 1, "TEST soggiorno", 272.73m, 10m, null));
        document.Lines.Add(Line(document, 2, "TEST pulizie", 45.45m, 10m, null));
        document.Lines.Add(Line(document, 3, "TEST imposta di soggiorno", 12.00m, 0m, "N1"));
        return document;
    }

    private static FiscalDocumentLine Line(FiscalDocument document, int number, string description, decimal amount, decimal rate, string? nature) => new()
    {
        OrgId = document.OrgId,
        DocumentId = document.Id,
        LineNumber = number,
        Description = description,
        Quantity = 1m,
        UnitPrice = amount,
        TotalPrice = amount,
        VatRatePercent = rate,
        VatNatureCode = nature,
    };

    private static OrgFiscalIssuerProfile Issuer() => new()
    {
        LegalName = "TEST Casa Vacanze Srl",
        AddressStreet = "Via di Test",
        AddressCivic = "1",
        PostalCode = "00187",
        City = "Roma",
        Province = "RM",
        CountryCode = "IT",
    };

    private static OrgEntity Org() => new()
    {
        Name = "TEST Casa Vacanze",
        Slug = "test-casa-vacanze",
        DisplayName = "TEST Casa Vacanze",
        ContactEmail = "test@example.com",
        HasPartitaIva = true,
        PartitaIvaNumber = "01234567897",
        FiscalCode = "01234567897",
    };
}
