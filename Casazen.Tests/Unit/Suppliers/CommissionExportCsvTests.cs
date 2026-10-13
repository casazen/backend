using System.Globalization;
using Casazen.Core.Services;
using Casazen.Core.Suppliers;
using Xunit;

namespace Casazen.Tests.Unit.Suppliers;

/// <summary>
/// SP-15b, decision D4: the monthly commission export is a plain CSV (RFC 4180, integer cents, ISO days) that the accountant
/// invoices from by hand while the VAT and DAC7 are open. A supplier chooses its own name, so a text that a spreadsheet would run
/// as a formula is neutralized; numbers are never touched (a refund is negative).
/// </summary>
public class CommissionExportCsvTests
{
    private static readonly Guid PaymentId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid RequestId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid SupplierId = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly Guid PayerId = Guid.Parse("44444444-4444-4444-4444-444444444444");

    private static CommissionExportRow Row(
        string type = CommissionExportRowType.Payment,
        string supplierName = "Idraulica Rossi Srl",
        string? vat = "IT01234567890",
        string? payerName = "Casa Verdi",
        long gross = 6_000,
        decimal percent = 10m,
        long commission = 600,
        long net = 5_400) =>
        new(type, new DateOnly(2026, 10, 9), PaymentId, RequestId, SupplierId, supplierName, vat, PayerId, payerName, "eur", gross, percent, commission, net);

    private static string[] Lines(string csv) => csv.Split("\r\n", StringSplitOptions.None);

    [Fact]
    public void Write_HasTheHeader_ThenOneLinePerRow_EachEndedByCrLf()
    {
        var csv = CommissionExportCsv.Write(new CommissionExport(2026, 10, null, [Row(), Row(CommissionExportRowType.Refund, gross: -1_500, commission: -150, net: -1_350)]));

        var lines = Lines(csv);
        Assert.Equal(4, lines.Length); // header, two rows, the empty piece after the last CRLF
        Assert.Equal(string.Empty, lines[^1]);
        Assert.Equal(string.Join(',', CommissionExportCsv.Columns), lines[0]);
        Assert.Equal(15, CommissionExportCsv.Columns.Count);
        Assert.DoesNotContain("\n", csv.Replace("\r\n", string.Empty, StringComparison.Ordinal));
    }

    [Fact]
    public void Write_APaymentLine_CarriesTheFiscalDataAndTheGross_InIntegerCents()
    {
        var csv = CommissionExportCsv.Write(new CommissionExport(2026, 10, null, [Row()]));

        Assert.Equal(
            "payment,2026-10-09,11111111-1111-1111-1111-111111111111,22222222-2222-2222-2222-222222222222,"
            + "33333333-3333-3333-3333-333333333333,Idraulica Rossi Srl,IT01234567890,44444444-4444-4444-4444-444444444444,"
            + "Casa Verdi,EUR,6000,10,600,,5400",
            Lines(csv)[1]);
    }

    [Fact]
    public void Write_ARefundLine_IsNegative_AndTheNegativeNumbersAreNotNeutralized()
    {
        var csv = CommissionExportCsv.Write(new CommissionExport(2026, 10, null, [Row(CommissionExportRowType.Refund, gross: -1_500, commission: -150, net: -1_350)]));

        var line = Lines(csv)[1];
        Assert.StartsWith("refund,", line, StringComparison.Ordinal);
        Assert.Contains(",-1500,10,-150,,-1350", line, StringComparison.Ordinal);
        Assert.DoesNotContain("'-", line, StringComparison.Ordinal);
    }

    [Fact]
    public void Write_TheVatRateOfTheConfiguration_IsRepeatedOnEveryLine_AndEmptyWhenNotDecided()
    {
        var decided = CommissionExportCsv.Write(new CommissionExport(2026, 10, 22m, [Row(), Row()]));
        var open = CommissionExportCsv.Write(new CommissionExport(2026, 10, null, [Row(), Row()]));

        Assert.All(Lines(decided).Skip(1).Where(l => l.Length > 0), l => Assert.Contains(",600,22,5400", l, StringComparison.Ordinal));
        // [CONSULENTE FISCALE]: while it is open the column is empty and no VAT amount is computed anywhere.
        Assert.All(Lines(open).Skip(1).Where(l => l.Length > 0), l => Assert.Contains(",600,,5400", l, StringComparison.Ordinal));
        Assert.DoesNotContain("vat_cents", string.Join(',', CommissionExportCsv.Columns));
    }

    [Theory]
    [InlineData("=HYPERLINK(\"http://evil\",\"x\")", "\"'=HYPERLINK(\"\"http://evil\"\",\"\"x\"\")\"")]
    [InlineData("+39 06 1234", "'+39 06 1234")]
    [InlineData("-cmd|' /C calc'!A0", "'-cmd|' /C calc'!A0")]
    [InlineData("@SUM(1+1)", "'@SUM(1+1)")]
    [InlineData("\tTabbed", "'\tTabbed")]
    public void Write_ANameThatASpreadsheetWouldRunAsAFormula_GetsALeadingApostrophe(string name, string writtenField)
    {
        var csv = CommissionExportCsv.Write(new CommissionExport(2026, 10, null, [Row(supplierName: name)]));

        // The field sits between the supplier org id and its P.IVA; the apostrophe comes before the formula character, and a field
        // with a comma or a quote is then quoted as usual.
        Assert.Contains("," + writtenField + ",IT01234567890,", Lines(csv)[1], StringComparison.Ordinal);
    }

    [Fact]
    public void Write_AnApostropheInTheMiddleOrAtTheStart_IsNotAFormulaAndIsLeftAlone()
    {
        var csv = CommissionExportCsv.Write(new CommissionExport(2026, 10, null, [Row(supplierName: "L'Idraulico", payerName: "'Casa")]));

        Assert.Contains(",L'Idraulico,", Lines(csv)[1], StringComparison.Ordinal);
        Assert.Contains(",'Casa,", Lines(csv)[1], StringComparison.Ordinal);
    }

    [Fact]
    public void Write_ATextWithACommaAQuoteOrANewline_IsQuotedWithTheQuotesDoubled()
    {
        var csv = CommissionExportCsv.Write(new CommissionExport(2026, 10, null, [Row(supplierName: "Rossi, \"Il Ponte\" & Figli\r\nSrl", payerName: "Casa, Verdi")]));

        var text = csv[(csv.IndexOf("\r\n", StringComparison.Ordinal) + 2)..];
        Assert.Contains("\"Rossi, \"\"Il Ponte\"\" & Figli\r\nSrl\"", text, StringComparison.Ordinal);
        Assert.Contains("\"Casa, Verdi\"", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Write_NumbersAreWrittenWithTheInvariantCulture_WhateverTheCultureOfTheServer()
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("it-IT");
            var csv = CommissionExportCsv.Write(new CommissionExport(2026, 10, 7.5m, [Row(percent: 7.5m, gross: 1_234_567, commission: 92_592, net: 1_141_975)]));

            var line = Lines(csv)[1];
            Assert.Contains(",1234567,7.5,92592,7.5,1141975", line, StringComparison.Ordinal);
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Fact]
    public void Write_ARowWithoutAPayerOrAVatNumber_LeavesThoseFieldsEmpty()
    {
        var csv = CommissionExportCsv.Write(new CommissionExport(
            2026, 10, null, [new CommissionExportRow(CommissionExportRowType.Payment, new DateOnly(2026, 10, 1), PaymentId, RequestId, SupplierId, "Idraulica", null, null, null, "eur", 100, 10m, 10, 90)]));

        Assert.Contains("Idraulica,,,,EUR,100,10,10,,90", Lines(csv)[1], StringComparison.Ordinal);
    }

    [Fact]
    public void Write_NoRows_IsJustTheHeader()
    {
        var csv = CommissionExportCsv.Write(new CommissionExport(2026, 10, null, []));

        Assert.Equal(string.Join(',', CommissionExportCsv.Columns) + "\r\n", csv);
    }

    [Fact]
    public void FileName_IsTheMonth()
    {
        Assert.Equal("commissioni-2026-10.csv", CommissionExportCsv.FileName(2026, 10));
        Assert.Equal("commissioni-2027-01.csv", CommissionExportCsv.FileName(2027, 1));
        Assert.Equal("text/csv", CommissionExportCsv.ContentType);
    }
}
