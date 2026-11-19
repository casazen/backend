using System.Text.Json;
using Casazen.Core.Documents;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;
using Casazen.Infrastructure.Services;
using Casazen.Web.DTOs.Fiscal;
using Casazen.Web.Resources;
using Xunit;
using DocumentKind = Casazen.Core.Entities.Enums.FiscalDocumentKind;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// FIX/BE-PORTAL-TAXLINE (finding of the Swiss acceptance scenario S025, money rule): the guest document of a booking of a portal is declared paid through the portal for what the portal
/// collected, never for the tourist tax the portal left to the host. The document keeps its lines, its total and its stored payment state; the PDF (both layouts, both languages) and the
/// document of the API say that the tax is to be collected at the property, and a document with no such line, and every document that is not the guest document of a portal, says what it said.
/// </summary>
public class PortalBookingDocumentTests
{
    private static readonly ResxDocumentTexts Texts = new();

    /// <summary>
    /// PROOF TEST: money rule. The stay of the finding (Booking.com through the channel manager, 450.00 collected by the portal, 12.00 of tourist tax of Ticino that nobody collected, total 462.00):
    /// 450.00 are paid through the portal and 12.00 are to collect at the property, in the Swiss layout and in the Italian one, in Italian and in English (different words), and in the document
    /// of the API; the total, the lines and the stored payment state are what they were.
    /// </summary>
    [Fact]
    public void PortalBookingDocument_UncollectedTouristTax_IsNotDeclaredPaid_Test()
    {
        var swiss = SwissStay(FiscalPaymentChannel.Portal);

        // The split: what the portal collected and what it did not, adding up to the total of the document.
        Assert.Equal(new PortalCollectionSplit(450.00m, 12.00m), FiscalDocumentPortalCollection.SplitOf(swiss));
        Assert.Equal((462.00m, FiscalDocumentPaymentState.Paid, FiscalPaymentChannel.Portal), (swiss.Total, swiss.PaymentState, swiss.PaymentChannel));

        // The Swiss layout, in Italian: the portal part and the tax in a paragraph each, the total as it was, the note on the line of the tax and on it only.
        var italian = FiscalDocumentPdfs.Pdf(swiss, SwissIssuer(), Texts, "it");
        Assert.Equal(new[] { "Pagata tramite il portale: CHF 450.00", "Tassa di soggiorno da riscuotere in struttura: CHF 12.00" }, BoldParagraphsOf(italian));
        Assert.Equal(
            new[] { "Soggiorno di 3 notti", "Tassa di soggiorno (non soggetta all'IVA) - tassa da riscuotere in struttura" },
            Assert.Single(italian.Blocks.OfType<PdfTable>(), t => t.Columns.Count == 5).Rows.Select(r => r[0]).ToArray());
        Assert.Equal(new[] { "445.53", "16.47", "462.00" }, Assert.Single(italian.Blocks.OfType<PdfTable>(), t => t.Columns.Count == 2).Rows.Select(r => r[1]).ToArray());

        // The same document in English: the words are the English ones (the stay line keeps the words it was stored with, as it always did).
        var english = FiscalDocumentPdfs.Pdf(swiss, SwissIssuer(), Texts, "en");
        Assert.Equal(new[] { "Paid through the portal: CHF 450.00", "Tourist tax to be collected at the property: CHF 12.00" }, BoldParagraphsOf(english));
        Assert.Equal(
            new[] { "Soggiorno di 3 notti", "Tourist tax (not subject to VAT) - tax to be collected at the property" },
            Assert.Single(english.Blocks.OfType<PdfTable>(), t => t.Columns.Count == 5).Rows.Select(r => r[0]).ToArray());

        // The Italian layout (a receipt of a private owner, 450.00 + 12.00, stamp duty not charged to the guest): the same split, in «Pagamento: …».
        var receipt = ItalianReceipt(FiscalPaymentChannel.Portal);
        Assert.Equal(new PortalCollectionSplit(450.00m, 12.00m), FiscalDocumentPortalCollection.SplitOf(receipt));
        var receiptIt = FiscalDocumentPdfs.Pdf(receipt, ItalianIssuer(), Texts, "it");
        Assert.Equal(new[] { "Pagamento: pagato tramite il portale per EUR 450,00", "Imposta di soggiorno da pagare in struttura: EUR 12,00" }, PaymentParagraphsOf(receiptIt));
        Assert.Equal(
            new[] { "TEST Soggiorno", "Imposta di soggiorno riscossa per conto del comune - da pagare in struttura" },
            Assert.Single(receiptIt.Blocks.OfType<PdfTable>(), t => t.Columns.Count == 6).Rows.Select(r => r[1]).ToArray());
        Assert.Contains(Assert.Single(receiptIt.Blocks.OfType<PdfTable>(), t => t.Columns.Count == 2).Rows, r => r[0] == "Totale documento" && r[1] == "462,00");
        Assert.Equal(
            new[] { "Payment: paid through the portal for EUR 450.00", "Tourist tax payable at the property: EUR 12.00" },
            PaymentParagraphsOf(FiscalDocumentPdfs.Pdf(receipt, ItalianIssuer(), Texts, "en")));

        // The document of the API: the parts and the flag of the line, the total and the stored state untouched; the members are in the JSON.
        var dto = FiscalDocumentDto.From(swiss);
        Assert.Equal((462.00m, "Paid", "Portal", (decimal?)450.00m, (decimal?)12.00m), (dto.Total, dto.PaymentState, dto.PaymentChannel, dto.AmountPaidViaPortal, dto.AmountToCollectAtProperty));
        Assert.Equal(new[] { false, true }, dto.Lines.Select(l => l.ToCollectAtProperty).ToArray());
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(dto, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        Assert.Equal((450.00m, 12.00m), (json.RootElement.GetProperty("amountPaidViaPortal").GetDecimal(), json.RootElement.GetProperty("amountToCollectAtProperty").GetDecimal()));
        Assert.Equal(new[] { false, true }, json.RootElement.GetProperty("lines").EnumerateArray().Select(l => l.TryGetProperty("toCollectAtProperty", out var flag) && flag.GetBoolean()).ToArray());
    }

    /// <summary>
    /// PROOF TEST: money rule. A portal document with nothing left to collect (the portal collects the tax: Airbnb in Ticino, no line), and every document that is not the guest document of a portal
    /// (collected through CasaZen or outside it, unknown channel, a credit note, a document of another source), says what it said: the same paragraph, the same lines, no note, and nothing new
    /// in the JSON of the API.
    /// </summary>
    [Fact]
    public void PortalBookingDocument_FullyCollected_IsUnchanged_Test()
    {
        // Everything collected by the portal: the stay only, «Pagata tramite il portale» for the whole document, in one bold paragraph, as before.
        var collected = SwissStay(FiscalPaymentChannel.Portal, withTax: false);
        Assert.Null(FiscalDocumentPortalCollection.SplitOf(collected));
        var content = FiscalDocumentPdfs.Pdf(collected, SwissIssuer(), Texts, "it");
        Assert.Equal(new[] { "Pagata tramite il portale" }, BoldParagraphsOf(content));
        Assert.Equal(new[] { "Soggiorno di 3 notti" }, Assert.Single(content.Blocks.OfType<PdfTable>(), t => t.Columns.Count == 5).Rows.Select(r => r[0]).ToArray());
        Assert.Equal(new[] { "Pagamento: pagato" }, PaymentParagraphsOf(FiscalDocumentPdfs.Pdf(ItalianReceipt(FiscalPaymentChannel.Portal, withTax: false), ItalianIssuer(), Texts, "it")));
        AssertNothingNewInTheApi(collected);

        // A tourist tax line on a document that is not the guest document of a portal is nobody's «to collect at the property»: the state of the document says what is collected.
        var others = new[]
        {
            (FiscalPaymentChannel.Outside, FiscalDocumentPaymentState.Paid, "Pagata", "Pagamento: pagato"),
            (FiscalPaymentChannel.CasaZen, FiscalDocumentPaymentState.ToCollect, "Da incassare", "Pagamento: da incassare"),
            (FiscalPaymentChannel.Unknown, FiscalDocumentPaymentState.ToCollect, "Da incassare", "Pagamento: da incassare"),
        };
        foreach (var (channel, state, swissPayment, italianPayment) in others)
        {
            var other = SwissStay(channel);
            other.PaymentState = state;
            Assert.Null(FiscalDocumentPortalCollection.SplitOf(other));
            var otherContent = FiscalDocumentPdfs.Pdf(other, SwissIssuer(), Texts, "it");
            Assert.Equal(new[] { swissPayment }, BoldParagraphsOf(otherContent));
            Assert.Equal(
                new[] { "Soggiorno di 3 notti", "Tassa di soggiorno (non soggetta all'IVA)" },
                Assert.Single(otherContent.Blocks.OfType<PdfTable>(), t => t.Columns.Count == 5).Rows.Select(r => r[0]).ToArray());
            AssertNothingNewInTheApi(other);

            var receipt = ItalianReceipt(channel);
            receipt.PaymentState = state;
            Assert.Equal(new[] { italianPayment }, PaymentParagraphsOf(FiscalDocumentPdfs.Pdf(receipt, ItalianIssuer(), Texts, "it")));
        }

        // The credit note of a portal document and a document CasaZen did not issue (an import) keep what they say too.
        var credit = SwissStay(FiscalPaymentChannel.Portal);
        credit.Kind = DocumentKind.CreditNote;
        Assert.Null(FiscalDocumentPortalCollection.SplitOf(credit));
        Assert.Empty(BoldParagraphsOf(FiscalDocumentPdfs.Pdf(credit, SwissIssuer(), Texts, "it")));
        AssertNothingNewInTheApi(credit);

        var creditReceipt = ItalianReceipt(FiscalPaymentChannel.Portal);
        creditReceipt.Kind = DocumentKind.CreditNote;
        Assert.Equal(new[] { "Pagamento: pagato" }, PaymentParagraphsOf(FiscalDocumentPdfs.Pdf(creditReceipt, ItalianIssuer(), Texts, "it")));

        var imported = SwissStay(FiscalPaymentChannel.Portal);
        imported.Source = FiscalDocumentSource.Octorate;
        Assert.Null(FiscalDocumentPortalCollection.SplitOf(imported));
        AssertNothingNewInTheApi(imported);

        // The row of a list carries no lines: nothing to derive, nothing new.
        var row = SwissStay(FiscalPaymentChannel.Portal);
        row.Lines.Clear();
        Assert.Null(FiscalDocumentPortalCollection.SplitOf(row));
    }

    /// <summary>
    /// A document that bills nothing but the tourist tax of a portal booking has nothing paid through the portal: only the tax to collect is written (never «paid through the portal for 0.00»), and the
    /// split never says more is to collect than the document is worth.
    /// </summary>
    [Fact]
    public void PortalBookingDocument_OnlyTheTax_HasNothingPaidThroughThePortal()
    {
        var document = SwissStay(FiscalPaymentChannel.Portal, withStay: false);

        Assert.Equal(new PortalCollectionSplit(0m, 12.00m), FiscalDocumentPortalCollection.SplitOf(document));
        Assert.Equal(new[] { "Tassa di soggiorno da riscuotere in struttura: CHF 12.00" }, BoldParagraphsOf(FiscalDocumentPdfs.Pdf(document, SwissIssuer(), Texts, "it")));

        var receipt = ItalianReceipt(FiscalPaymentChannel.Portal, withStay: false);
        Assert.Equal(new[] { "Pagamento: da incassare", "Imposta di soggiorno da pagare in struttura: EUR 12,00" }, PaymentParagraphsOf(FiscalDocumentPdfs.Pdf(receipt, ItalianIssuer(), Texts, "it")));

        document.Total = 10.00m;
        Assert.Equal(new PortalCollectionSplit(0m, 10.00m), FiscalDocumentPortalCollection.SplitOf(document));
    }

    /// <summary>
    /// PROOF TEST: money rule (review of FIX/BE-PORTAL-TAXLINE). The tourist tax the collection of the stay (E25) records as collected is not «to collect at the property»: what the record says is
    /// collected (all of the tax once the host recorded it paid at the property; what the guest paid online net of the refunds, never more than the tax) comes off the part to collect, in both layouts and in
    /// the document of the API. With nothing recorded the document says what it said (450.00 through the portal, 12.00 to collect); with all of the tax recorded it says what it said when no tax was left.
    /// </summary>
    [Fact]
    public void PortalBookingDocument_TouristTaxRecordedAsCollected_IsNotToCollect_Test()
    {
        var swiss = SwissStay(FiscalPaymentChannel.Portal);
        var receipt = ItalianReceipt(FiscalPaymentChannel.Portal);

        // What the record says is collected, never more than the tax of the document, and nothing for a document that is not the guest document of a portal.
        Assert.Equal(12.00m, FiscalDocumentPortalCollection.CollectedOf(swiss, TouristTaxCollectionStatus.PaidAtProperty, 0m, 0m));
        Assert.Equal(5.00m, FiscalDocumentPortalCollection.CollectedOf(swiss, TouristTaxCollectionStatus.PaidOnline, 5.00m, 0m));
        Assert.Equal(3.00m, FiscalDocumentPortalCollection.CollectedOf(swiss, TouristTaxCollectionStatus.PaidOnline, 5.00m, 2.00m));
        Assert.Equal(12.00m, FiscalDocumentPortalCollection.CollectedOf(swiss, TouristTaxCollectionStatus.PaidOnline, 20.00m, 0m));
        Assert.Equal(0m, FiscalDocumentPortalCollection.CollectedOf(swiss, TouristTaxCollectionStatus.PaidOnline, 5.00m, 5.00m));
        Assert.Equal(0m, FiscalDocumentPortalCollection.CollectedOf(SwissStay(FiscalPaymentChannel.Outside), TouristTaxCollectionStatus.PaidAtProperty, 0m, 0m));

        // Part of it recorded (5.00 paid online): only the rest is to collect, the line keeps its note, and the portal part is what the portal collected.
        swiss.TouristTaxCollected = 5.00m;
        receipt.TouristTaxCollected = 5.00m;
        Assert.Equal(new PortalCollectionSplit(450.00m, 7.00m), FiscalDocumentPortalCollection.SplitOf(swiss));
        Assert.Equal(new[] { "Pagata tramite il portale: CHF 450.00", "Tassa di soggiorno da riscuotere in struttura: CHF 7.00" }, BoldParagraphsOf(FiscalDocumentPdfs.Pdf(swiss, SwissIssuer(), Texts, "it")));
        Assert.Equal(new[] { "Pagamento: pagato tramite il portale per EUR 450,00", "Imposta di soggiorno da pagare in struttura: EUR 7,00" }, PaymentParagraphsOf(FiscalDocumentPdfs.Pdf(receipt, ItalianIssuer(), Texts, "it")));
        var partial = FiscalDocumentDto.From(swiss);
        Assert.Equal(((decimal?)450.00m, (decimal?)7.00m), (partial.AmountPaidViaPortal, partial.AmountToCollectAtProperty));
        Assert.Equal(new[] { false, true }, partial.Lines.Select(l => l.ToCollectAtProperty).ToArray());

        // All of it recorded (paid at the property, or paid online in full): nothing is to collect, no note, and the document says what it said when no tax was left to the host.
        foreach (var collected in new[] { 12.00m, 20.00m })
        {
            swiss.TouristTaxCollected = collected;
            receipt.TouristTaxCollected = collected;
            Assert.Null(FiscalDocumentPortalCollection.SplitOf(swiss));
            var content = FiscalDocumentPdfs.Pdf(swiss, SwissIssuer(), Texts, "it");
            Assert.Equal(new[] { "Pagata tramite il portale" }, BoldParagraphsOf(content));
            Assert.Equal(
                new[] { "Soggiorno di 3 notti", "Tassa di soggiorno (non soggetta all'IVA)" },
                Assert.Single(content.Blocks.OfType<PdfTable>(), t => t.Columns.Count == 5).Rows.Select(r => r[0]).ToArray());
            Assert.Equal(new[] { "Pagamento: pagato" }, PaymentParagraphsOf(FiscalDocumentPdfs.Pdf(receipt, ItalianIssuer(), Texts, "it")));
            AssertNothingNewInTheApi(swiss);
        }

        // Nothing recorded: as before the collection was read.
        swiss.TouristTaxCollected = 0m;
        Assert.Equal(new PortalCollectionSplit(450.00m, 12.00m), FiscalDocumentPortalCollection.SplitOf(swiss));
    }

    /// <summary>The members the API gained are absent from the answer of a document they do not concern: its JSON is what it was.</summary>
    private static void AssertNothingNewInTheApi(FiscalDocument document)
    {
        var dto = FiscalDocumentDto.From(document);
        Assert.Null(dto.AmountPaidViaPortal);
        Assert.Null(dto.AmountToCollectAtProperty);
        Assert.All(dto.Lines, l => Assert.False(l.ToCollectAtProperty));

        var json = JsonSerializer.Serialize(dto, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.DoesNotContain("amountPaidViaPortal", json, StringComparison.Ordinal);
        Assert.DoesNotContain("amountToCollectAtProperty", json, StringComparison.Ordinal);
        Assert.DoesNotContain("toCollectAtProperty", json, StringComparison.Ordinal);
    }

    private static string[] BoldParagraphsOf(PdfDocumentContent content) =>
        content.Blocks.OfType<PdfParagraph>().Where(p => p.Bold).Select(p => p.Text).ToArray();

    /// <summary>The paragraphs about the payment of an Italian document: «Pagamento: …» and the tax to collect.</summary>
    private static string[] PaymentParagraphsOf(PdfDocumentContent content) =>
        content.Blocks.OfType<PdfParagraph>()
            .Select(p => p.Text)
            .Where(t => t.StartsWith("Pagamento:", StringComparison.Ordinal) || t.StartsWith("Payment:", StringComparison.Ordinal) || t.Contains("payable at the property", StringComparison.Ordinal)
                        || t.Contains("da pagare in struttura", StringComparison.Ordinal))
            .ToArray();

    /// <summary>
    /// The Swiss document of the finding made the way the service makes it (<see cref="FiscalDocumentTotals.FromGross"/>): the stay of 450.00 at 3.8% (net 433.53, VAT 16.47) and the tourist tax of
    /// Ticino, 12.00 outside the VAT, 462.00 in all, stored as paid when it came from the portal.
    /// </summary>
    private static FiscalDocument SwissStay(FiscalPaymentChannel channel, bool withTax = true, bool withStay = true)
    {
        var lines = new List<FiscalDocumentGrossLine>();
        if (withStay)
            lines.Add(new FiscalDocumentGrossLine("Soggiorno di 3 notti", 1m, 450m, 3.8m, null, FiscalLineType.Accommodation));
        if (withTax)
            lines.Add(new FiscalDocumentGrossLine("Tassa di soggiorno (non soggetta all'IVA)", 1m, 12m, 0m, null, FiscalLineType.TouristTax));

        return DocumentOf(FiscalDocumentTotals.FromGross(lines, null), "CHF", DocumentKind.Invoice, channel, "2026/1");
    }

    /// <summary>The receipt of a private owner for the same stay in Italy (450.00 without VAT, N2.2, and the tourist tax N1 12.00): the stamp duty of 2.00 is the owner's, so the total is 462.00.</summary>
    private static FiscalDocument ItalianReceipt(FiscalPaymentChannel channel, bool withTax = true, bool withStay = true)
    {
        var lines = new List<FiscalDocumentGrossLine>();
        if (withStay)
            lines.Add(new FiscalDocumentGrossLine("TEST Soggiorno", 1m, 450m, 0m, "N2.2", FiscalLineType.Accommodation));
        if (withTax)
            lines.Add(new FiscalDocumentGrossLine("Imposta di soggiorno riscossa per conto del comune", 1m, 12m, 0m, "N1", FiscalLineType.TouristTax));

        return DocumentOf(FiscalDocumentTotals.FromGross(lines, new FiscalStampDutyRule(77.47m, 2.00m, ChargedToRecipient: false)), "EUR", DocumentKind.Receipt, channel, "RIC2026/1");
    }

    private static FiscalDocument DocumentOf(FiscalDocumentAmounts amounts, string currency, DocumentKind kind, FiscalPaymentChannel channel, string number)
    {
        var document = new FiscalDocument
        {
            OrgId = Guid.NewGuid(),
            Kind = kind,
            Status = FiscalDocumentStatus.Issued,
            Series = "2026",
            SequenceNumber = 1,
            Number = number,
            IssueDate = new DateOnly(2026, 10, 6),
            Currency = currency,
            RecipientName = "TEST Ospite Portale",
            ServicePeriodFrom = new DateOnly(2026, 10, 2),
            ServicePeriodTo = new DateOnly(2026, 10, 5),
            TaxableBase = amounts.TaxableBase,
            VatAmount = amounts.VatAmount,
            StampDuty = amounts.StampDuty,
            Total = amounts.Total,
            PaymentState = FiscalDocumentPaymentState.Paid,
            PaymentChannel = channel,
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

    private static FiscalIssuerSnapshot SwissIssuer() => new()
    {
        LegalName = "TEST Quokka Sagl",
        AddressStreet = "Via Nassa",
        AddressCivic = "5",
        PostalCode = "6900",
        City = "Lugano",
        CountryCode = "CH",
        VatRegistered = true,
        SwissVatNumber = "CHE-109.322.551",
        Regime = DocumentRegime.SwissVatRegistered,
    };

    private static FiscalIssuerSnapshot ItalianIssuer() => new()
    {
        LegalName = "TEST Mario Rossi",
        CountryCode = "IT",
        FiscalCode = "RSSMRA70A01H501S",
        Regime = DocumentRegime.PrivatePerson,
        VirtualStampConfirmed = true,
    };
}
