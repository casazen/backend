using System.Text;
using Casazen.Core.Services;
using Casazen.Infrastructure.Services;
using Xunit;

namespace Casazen.Tests.Unit.Services;

public sealed class TouristTaxSummaryRulesTests
{
    private static readonly FiscalReportPeriod FirstQuarter = new(new DateOnly(2026, 1, 1), new DateOnly(2026, 3, 31));

    private static readonly TouristTaxCsvTexts Texts = new("Nessun importo registrato", "Importo incompleto");

    /// <summary>
    /// PROOF TEST: money rule. A comune whose stays have no recorded amount is "Nessun importo registrato", never "niente da versare", and
    /// every row (and the total) that counts such stays says "Importo incompleto" next to the owed amount and the status, also when the status
    /// is "Versata": an amount that is missing is not a tax of zero.
    /// </summary>
    [Fact]
    public void TouristTaxSummaryCsv_AMissingAmount_IsNeverSaidAsNothingDue_Test()
    {
        var summary = new TouristTaxSummary(
            FirstQuarter,
            [
                new ComuneTaxSummary("Lecco", 2, 5, 4, 0m, 2, 0m, 0m, TouristTaxSummaryStatus.NothingOwed, []),
                new ComuneTaxSummary("Milano", 3, 6, 6, 66.90m, 1, 66.90m, 0m, TouristTaxSummaryStatus.Paid, []),
                new ComuneTaxSummary("Roma", 1, 4, 2, 72.10m, 0, 0m, 72.10m, TouristTaxSummaryStatus.ToPay, []),
            ],
            new TouristTaxSummaryTotals(6, 15, 12, 139.00m, 3, 66.90m, 72.10m));

        var csv = Lines(TouristTaxSummaryDocuments.Csv(summary, Texts));

        Assert.Equal("Lecco;2;5;4;0,00;2;0,00;0,00;Nessun importo registrato;Importo incompleto", csv[2]);
        Assert.Equal("Milano;3;6;6;66,90;1;66,90;0,00;Versata;Importo incompleto", csv[3]);
        Assert.Equal("Roma;1;4;2;72,10;0;0,00;72,10;Da versare;", csv[4]);
        Assert.Equal("Totale;6;15;12;139,00;3;66,90;72,10;;Importo incompleto", csv[5]);
        Assert.DoesNotContain(csv, line => line.Contains("Niente da versare", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("12.50", 1250)]
    [InlineData("12.5", 1250)]
    [InlineData("12", 1200)]
    [InlineData("0.05", 5)]
    public void TouristTaxAmount_ADotIsTheDecimalPoint_Test(string text, int expectedCents)
    {
        Assert.True(TouristTaxSummaryRules.TryParseAmount(text, out var amount));
        Assert.Equal(expectedCents, PaymentCashRules.ToCents(amount));
    }

    [Theory]
    [InlineData("12,50")]
    [InlineData("1.250,00")]
    [InlineData("1,250.00")]
    [InlineData("+12.50")]
    [InlineData("-12.50")]
    [InlineData("1e3")]
    [InlineData("1E3")]
    [InlineData("12.345")]
    [InlineData("12.")]
    [InlineData(".5")]
    [InlineData("1 250")]
    [InlineData("")]
    [InlineData("   ")]
    public void TouristTaxAmount_AnythingButDigitsAndOneDot_IsRefused_Test(string text)
    {
        Assert.False(TouristTaxSummaryRules.TryParseAmount(text, out var amount));
        Assert.Equal(0m, amount);
    }

    [Fact]
    public void TouristTaxReceipt_IsWhatItsFirstBytesSay_Test()
    {
        Assert.Equal(TouristTaxReceiptRules.PdfKind, TouristTaxReceiptRules.Sniff("%PDF-1.7\n"u8));
        Assert.Equal(TouristTaxReceiptRules.JpegKind, TouristTaxReceiptRules.Sniff(new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 0x00 }));
        Assert.Equal(
            TouristTaxReceiptRules.PngKind,
            TouristTaxReceiptRules.Sniff(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00 }));

        Assert.Null(TouristTaxReceiptRules.Sniff("ricevuta.pdf"u8));
        Assert.Null(TouristTaxReceiptRules.Sniff("GIF89a"u8));
        Assert.Null(TouristTaxReceiptRules.Sniff(new byte[] { 0x50, 0x4B, 0x03, 0x04 }));
        Assert.Null(TouristTaxReceiptRules.Sniff(new byte[] { 0x89, 0x50, 0x4E, 0x47 }));
        Assert.Null(TouristTaxReceiptRules.Sniff(ReadOnlySpan<byte>.Empty));
    }

    [Fact]
    public void TouristTaxSummaryCsv_FirstRowCarriesTheItalianTitleWithItsBaseAndTheDays_Test()
    {
        var summary = new TouristTaxSummary(FirstQuarter, [], new TouristTaxSummaryTotals(0, 0, 0, 0m, 0, 0m, 0m));

        var first = Lines(TouristTaxSummaryDocuments.Csv(summary, Texts))[0];

        Assert.Equal(
            "Tassa di soggiorno per comune: dovuta sui soggiorni con check-in nel periodo, versata con periodo compreso nel periodo, dal 01/01/2026 al 31/03/2026",
            first);
    }

    private static string[] Lines(byte[] csv)
    {
        using var reader = new StreamReader(new MemoryStream(csv), Encoding.UTF8);
        return reader.ReadToEnd().Split("\r\n", StringSplitOptions.None);
    }
}
