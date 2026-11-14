using Casazen.Core.Documents;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;
using Casazen.Infrastructure.Documents;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// E14/F01/US01/T02 (NF-113, NF-114): the Swiss QR-bill of an owner invoice. The payload follows the Swiss Implementation Guidelines (the order of the fields,
/// structured addresses, the QR reference with its recursive modulo-10 check digit, the creditor reference of ISO 11649), and the PDF of an issued Swiss owner
/// invoice ends with the payment part only when an amount is left for the owner to pay: offset in the statement, or without an account of the agency, it says so.
/// </summary>
public class SwissQrBillTests
{
    /// <summary>
    /// PROOF TEST: money rule. The example of the guidelines (QR-IBAN CH44 3199 9123 0008 8901 2, QR reference 21 00000 00003 13947 14300 09017) gives the
    /// payload of the guidelines field by field, and its check digit 7; the creditor reference RF18 5390 0754 7034 has its check digits 18. A QR-IBAN takes only a
    /// QR reference, an IBAN only a creditor reference or none, CHF and EUR only, an account of CH or LI only: anything else is no QR-bill.
    /// </summary>
    [Fact]
    public void SwissQrBill_PayloadOrderAndQrrCheckDigit_Test()
    {
        Assert.Equal(7, SwissQrBill.QrrCheckDigit("21000000000313947143000901"));
        Assert.Equal("210000000003139471430009017", SwissQrBill.QrrReference("21000000000313947143000901"));
        Assert.True(SwissQrBill.IsValidQrr("210000000003139471430009017"));
        Assert.False(SwissQrBill.IsValidQrr("210000000003139471430009016"));
        Assert.Equal("RF18539007547034", SwissQrBill.ScorReference("5390 0754 7034"));
        Assert.True(SwissQrBill.IsValidScor("RF18539007547034"));
        Assert.False(SwissQrBill.IsValidScor("RF19539007547034"));

        var example = new SwissQrBillData(
            "CH4431999123000889012",
            new SwissQrBillAddress("Robert Schneider AG", "Rue du Lac", "1268", "2501", "Biel", "CH"),
            1949.75m,
            "CHF",
            new SwissQrBillAddress("Pia-Maria Rutschmann-Schnyder", "Grosse Marktgasse", "28", "9400", "Rorschach", "CH"),
            SwissQrBill.ReferenceQrr,
            "210000000003139471430009017",
            "Order of 15 June 2020");

        Assert.Equal(
            string.Join(
                '\n',
                "SPC", "0200", "1", "CH4431999123000889012",
                "S", "Robert Schneider AG", "Rue du Lac", "1268", "2501", "Biel", "CH",
                string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty,
                "1949.75", "CHF",
                "S", "Pia-Maria Rutschmann-Schnyder", "Grosse Marktgasse", "28", "9400", "Rorschach", "CH",
                "QRR", "210000000003139471430009017", "Order of 15 June 2020",
                "EPD"),
            SwissQrBill.Build(example));

        Assert.Equal("reference", SwissQrBill.Problem(example with { ReferenceType = SwissQrBill.ReferenceScor, Reference = "RF18539007547034" }));
        Assert.Equal("reference", SwissQrBill.Problem(example with { Reference = "210000000003139471430009016" }));
        Assert.Equal("currency", SwissQrBill.Problem(example with { Currency = "USD" }));
        Assert.Equal("account", SwissQrBill.Problem(example with { Account = "IT60X0542811101000000123456" }));
        Assert.Equal("debtor", SwissQrBill.Problem(example with { Debtor = new SwissQrBillAddress("TEST", null, null, string.Empty, "Lugano", "CH") }));
        Assert.Null(SwissQrBill.Problem(example with
        {
            Account = "CH5800791123000889012",
            ReferenceType = SwissQrBill.ReferenceScor,
            Reference = "RF18539007547034",
            Debtor = null,
            Amount = null,
        }));
        Assert.Equal(("21 00000 00003 13947 14300 09017", "CH44 3199 9123 0008 8901 2", "1 949.75"), (
            SwissQrBill.FormatReference(SwissQrBill.ReferenceQrr, "210000000003139471430009017"),
            SwissQrBill.FormatIban("CH4431999123000889012"),
            SwissQrBill.FormatAmount(1949.75m)));
    }

    /// <summary>
    /// An issued Swiss owner invoice that leaves CHF 1,081.00 to pay (a commission with nothing to offset it) ends with the QR-bill of that amount: the QR-IBAN of
    /// the agency with a QR reference, the agency and the owner as structured addresses, «Fattura PM2026/1»; the PDF has the page of the payment part.
    /// </summary>
    [Fact]
    public async Task SwissQrBill_OwnerInvoiceWithAnAmountDue_EndsWithThePaymentPart_Test()
    {
        await using var rig = await OwnerInvoiceRig.SwissAsync();
        var version = await rig.VersionAsync([rig.Line(OwnerLedgerEntryType.AgencyCommission, -1000.00m, rig.LakeHouse)]);
        var issued = await rig.Invoices.IssueAsync(rig.OrgId, (await rig.Invoices.CreateDraftAsync(rig.OrgId, version.Id, null)).Id, confirm: true, null);

        var content = await rig.Documents.PdfContentAsync(issued.Id, DocumentLanguages.Italian);

        var bill = Assert.IsType<PdfSwissQrBill>(content.QrBill);
        var fields = bill.Payload.Split('\n');
        Assert.Equal(31, fields.Length);
        Assert.Equal(("SPC", "0200", "1", "CH4431999123000889012"), (fields[0], fields[1], fields[2], fields[3]));
        Assert.Equal(("S", "TEST Gestioni Lugano Sagl", "6900", "Lugano", "CH"), (fields[4], fields[5], fields[8], fields[9], fields[10]));
        Assert.Equal(("1081.00", "CHF"), (fields[18], fields[19]));
        Assert.Equal(("S", "TEST Proprietario", "Via dei Test", "7", "6600", "Locarno", "CH"), (fields[20], fields[21], fields[22], fields[23], fields[24], fields[25], fields[26]));
        Assert.Equal(SwissQrBill.ReferenceQrr, fields[27]);
        Assert.True(SwissQrBill.IsValidQrr(fields[28]));
        Assert.Equal(("Fattura PM2026/1", "EPD"), (fields[29], fields[30]));
        Assert.Contains("Importo da pagare: CHF 1'081.00", TextOf(content), StringComparison.Ordinal);

        var pdf = new MigraDocPdfDocumentRenderer().Render(content);
        using var document = UglyToad.PdfPig.PdfDocument.Open(pdf);
        Assert.Equal(2, document.NumberOfPages);
        var payment = document.GetPage(2).Text;
        Assert.Contains("Sezione pagamento", payment, StringComparison.Ordinal);
        Assert.Contains("Ricevuta", payment, StringComparison.Ordinal);
    }

    /// <summary>
    /// An invoice the statement offsets gets no QR-bill and «Compensata nel rendiconto del 10/2026»; an invoice left to pay by an agency without an IBAN or a
    /// QR-IBAN gets no QR-bill either and «Pagamento su indicazione del gestore».
    /// </summary>
    [Fact]
    public async Task SwissQrBill_OffsetInvoiceOrAgencyWithoutAccount_GetsTheNoteAndNoQrBill_Test()
    {
        await using (var offset = await OwnerInvoiceRig.SwissAsync())
        {
            var version = await offset.VersionAsync(offset.StandardMonth());
            var issued = await offset.Invoices.IssueAsync(offset.OrgId, (await offset.Invoices.CreateDraftAsync(offset.OrgId, version.Id, null)).Id, confirm: true, null);

            var content = await offset.Documents.PdfContentAsync(issued.Id, DocumentLanguages.Italian);

            Assert.Null(content.QrBill);
            Assert.Contains("Compensata nel rendiconto del 10/2026", TextOf(content), StringComparison.Ordinal);
        }

        await using var noAccount = await OwnerInvoiceRig.SwissAsync();
        var profile = await noAccount.Db.OrgFiscalIssuerProfiles.SingleAsync();
        profile.Iban = null;
        profile.QrIban = null;
        await noAccount.Db.SaveChangesAsync();
        var due = await noAccount.VersionAsync([noAccount.Line(OwnerLedgerEntryType.AgencyCommission, -1000.00m, noAccount.LakeHouse)]);
        var unpaid = await noAccount.Invoices.IssueAsync(noAccount.OrgId, (await noAccount.Invoices.CreateDraftAsync(noAccount.OrgId, due.Id, null)).Id, confirm: true, null);

        var noBill = await noAccount.Documents.PdfContentAsync(unpaid.Id, DocumentLanguages.Italian);

        Assert.Null(noBill.QrBill);
        var text = TextOf(noBill);
        Assert.Contains("Importo da pagare: CHF 1'081.00", text, StringComparison.Ordinal);
        Assert.Contains("Pagamento su indicazione del gestore", text, StringComparison.Ordinal);
    }

    /// <summary>Every text of a PDF content: headings, paragraphs and the cells of the tables, one per line.</summary>
    private static string TextOf(PdfDocumentContent content) =>
        string.Join('\n', new[] { content.Title }.Concat(content.Blocks.SelectMany(TextsOf)));

    private static IEnumerable<string> TextsOf(PdfBlock block) => block switch
    {
        PdfHeading heading => [heading.Text],
        PdfParagraph paragraph => [paragraph.Text],
        PdfTable table => table.Columns.Select(c => c.Header).Concat(table.Rows.SelectMany(r => r)),
        _ => [],
    };
}
