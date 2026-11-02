using Casazen.Core.BankStatements;
using Xunit;

namespace Casazen.Tests.Unit.BankStatements;

/// <summary>The values a bank file holds (amounts, dates, IBANs, the months a reason names): each bank writes them its own way.</summary>
public class BankValuesTests
{
    [Theory]
    [InlineData("1.234,56", 1234.56)]
    [InlineData("1,234.56", 1234.56)]
    [InlineData("900,00", 900)]
    [InlineData("900.00", 900)]
    [InlineData("900", 900)]
    [InlineData("-12,50", -12.50)]
    [InlineData("12,50-", -12.50)]
    [InlineData("(12,50)", -12.50)]
    [InlineData("€ 12,50", 12.50)]
    [InlineData("12,50 EUR", 12.50)]
    [InlineData("EUR 1.250,00", 1250)]
    [InlineData("12,50 DR", -12.50)]
    [InlineData("12,50 CR", 12.50)]
    [InlineData("+900,00", 900)]
    [InlineData("1.234", 1234)]
    [InlineData("1.234.567,89", 1234567.89)]
    [InlineData("12,5", 12.5)]
    [InlineData("0,5", 0.5)]
    [InlineData(",50", 0.5)]
    [InlineData("1\u00A0234,56", 1234.56)]
    [InlineData("1 234,56", 1234.56)]
    public void TryParseAmount_TheWaysBanksWriteAnAmount_AreRead(string text, double expected)
    {
        var read = BankValues.TryParseAmount(new Cell(text, false), DecimalMark.Comma, out var amount);

        Assert.True(read, text);
        Assert.Equal(decimal.Round((decimal)expected, 2), amount);
    }

    [Theory]
    [InlineData("1,234", "point", 1234)]
    [InlineData("1,234", "comma", 1.234)]
    [InlineData("12,5", "point", 12.5)]
    [InlineData("900.00", "point", 900)]
    [InlineData("1.234", "point", 1.234)]
    public void TryParseAmount_AnAmbiguousMark_FollowsTheMarkTheColumnUses(string text, string markName, double expected)
    {
        var mark = markName == "point" ? DecimalMark.Point : DecimalMark.Comma;

        var read = BankValues.TryParseAmount(new Cell(text, false), mark, out var amount);

        Assert.True(read);
        Assert.Equal(Math.Round((decimal)expected, 2, MidpointRounding.AwayFromZero), amount);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("abc")]
    [InlineData("-")]
    [InlineData("n/d")]
    public void TryParseAmount_WhatIsNotAnAmount_IsNotRead(string text)
    {
        Assert.False(BankValues.TryParseAmount(new Cell(text, false), DecimalMark.Comma, out _));
    }

    [Fact]
    public void TryParseAmount_ANumberStoredByExcel_IsTakenAsItIs()
    {
        Assert.True(BankValues.TryParseAmount(new Cell("1234.5", true), DecimalMark.Comma, out var amount));
        Assert.Equal(1234.50m, amount);
    }

    [Fact]
    public void DetectDecimalMark_ReadsTheMarkTheColumnUses_AndDefaultsToTheItalianOne()
    {
        Assert.Equal(DecimalMark.Comma, BankValues.DetectDecimalMark([new Cell("1.234,56", false), new Cell("12,50", false)]));
        Assert.Equal(DecimalMark.Point, BankValues.DetectDecimalMark([new Cell("1,234.56", false), new Cell("12.50", false)]));
        Assert.Equal(DecimalMark.Comma, BankValues.DetectDecimalMark([new Cell("900", false), new Cell("1.200", false)]));
        Assert.Equal(DecimalMark.Comma, BankValues.DetectDecimalMark([]));
    }

    [Theory]
    [InlineData("03/10/2026")]
    [InlineData("3/10/2026")]
    [InlineData("03-10-2026")]
    [InlineData("03.10.2026")]
    [InlineData("03/10/26")]
    [InlineData("2026-10-03")]
    [InlineData("2026/10/03")]
    [InlineData("20261003")]
    [InlineData("03/10/2026 14:35")]
    [InlineData("03/10/2026 14:35:10")]
    [InlineData("2026-10-03T14:35:10")]
    [InlineData("2026-10-03T00:00:00+02:00")]
    [InlineData("2026-10-03T23:30:00-05:00")]
    [InlineData("3 ott 2026")]
    public void TryParseDate_TheWaysBanksWriteADate_AreRead_AndNeverMovedToUtc(string text)
    {
        Assert.True(BankValues.TryParseDate(new Cell(text, false), out var date), text);
        Assert.Equal(new DateOnly(2026, 10, 3), date);
    }

    [Fact]
    public void TryParseDate_AnExcelSerial_IsADate()
    {
        var serial = (new DateTime(2026, 10, 3) - new DateTime(1899, 12, 30)).TotalDays;

        Assert.True(BankValues.TryParseDate(new Cell(serial.ToString(System.Globalization.CultureInfo.InvariantCulture), true), out var fromNumber));
        Assert.Equal(new DateOnly(2026, 10, 3), fromNumber);
        Assert.True(BankValues.TryParseDate(new Cell(((int)serial).ToString(System.Globalization.CultureInfo.InvariantCulture), false), out var fromText));
        Assert.Equal(new DateOnly(2026, 10, 3), fromText);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Saldo iniziale")]
    [InlineData("32/13/2026")]
    [InlineData("01/01/1999")]
    [InlineData("01/01/2101")]
    [InlineData("12345")]
    public void TryParseDate_WhatIsNotADateOfARent_IsNotRead(string text)
    {
        Assert.False(BankValues.TryParseDate(new Cell(text, false), out _));
    }

    [Fact]
    public void FindIban_WithOrWithoutTheSpacesOfTheGroups_ReturnsTheValidOne()
    {
        const string iban = "IT60X0542811101000000123456";

        Assert.Equal(iban, BankValues.FindIban("BONIFICO DA ROSSI IBAN IT60X0542811101000000123456 CANONE"));
        Assert.Equal(iban, BankValues.FindIban("bonifico iban it60 x054 2811 1010 0000 0123 456 canone ottobre"));
        Assert.Equal(iban, BankValues.FindIban("IT60 X054 2811 1010 0000 0123 456"));
    }

    [Fact]
    public void FindIban_ACodeWhoseCheckDigitsAreWrong_IsNotAnIban()
    {
        Assert.Null(BankValues.FindIban("riferimento IT60X0542811101000000123457 canone"));
        Assert.Null(BankValues.FindIban("canone di ottobre senza iban"));
        Assert.Null(BankValues.FindIban(null));
    }

    [Fact]
    public void NormalizeIban_AFieldOfItsOwn_IsUpperCaseWithoutSpaces_OnlyIfItChecks()
    {
        Assert.Equal("IT60X0542811101000000123456", BankValues.NormalizeIban(" it60 x054 2811 1010 0000 0123 456 "));
        Assert.Null(BankValues.NormalizeIban("IT60X0542811101000000123457"));
        Assert.Null(BankValues.NormalizeIban("Mario Rossi"));
        Assert.Null(BankValues.NormalizeIban(""));
    }

    [Theory]
    [InlineData("Perché D'Angelo", "perche d angelo")]
    [InlineData("  VERDI/GIULIA  ", "verdi giulia")]
    [InlineData("Via   Lunga, 12", "via lunga 12")]
    [InlineData("", "")]
    public void NormalizeText_LowerCaseNoAccentsOneSpace(string text, string expected)
    {
        Assert.Equal(expected, BankValues.NormalizeText(text));
    }

    [Theory]
    [InlineData("Canone locazione ottobre 2026", 10, 2026)]
    [InlineData("AFFITTO 10/2026", 10, 2026)]
    [InlineData("affitto 10-2026", 10, 2026)]
    [InlineData("competenza 2026-10", 10, 2026)]
    [InlineData("canone ottobre", 10, null)]
    [InlineData("mese di set", 9, null)]
    [InlineData("rata ott 2026", 10, 2026)]
    [InlineData("canone gennaio 2027", 1, 2027)]
    [InlineData("rent october 2026", 10, 2026)]
    public void ExtractMonths_ReadsTheMonthAPaymentIsFor(string reason, int month, int? year)
    {
        var months = BankValues.ExtractMonths(BankValues.NormalizeText(reason));

        Assert.Contains(new MonthReference(month, year), months);
    }

    [Theory]
    [InlineData("bonifico del 03/10/2026")]
    [InlineData("pagamento del 3 ottobre 2026")]
    [InlineData("data operazione 03 10 2026")]
    [InlineData("Verdi Giulia")]
    [InlineData("set di pentole")]
    [InlineData("12 ottobre 2026")]
    public void ExtractMonths_TheDayOfThePaymentOrAPlainWord_IsNotAReference(string reason)
    {
        Assert.Empty(BankValues.ExtractMonths(BankValues.NormalizeText(reason)));
    }

    [Fact]
    public void ReferencesPeriod_TheMonthsOfTheInstallment_AreMatchedWithOrWithoutTheYear()
    {
        var october = new[] { new MonthReference(10, 2026) };
        var anyOctober = new[] { new MonthReference(10, null) };

        Assert.True(BankValues.ReferencesPeriod(october, new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 31)));
        Assert.True(BankValues.ReferencesPeriod(anyOctober, new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 31)));
        Assert.False(BankValues.ReferencesPeriod(october, new DateOnly(2025, 10, 1), new DateOnly(2025, 10, 31)));
        Assert.False(BankValues.ReferencesPeriod(october, new DateOnly(2026, 11, 1), new DateOnly(2026, 11, 30)));
        // A quarterly installment is for any of its months.
        Assert.True(BankValues.ReferencesPeriod(october, new DateOnly(2026, 8, 1), new DateOnly(2026, 10, 31)));
        Assert.False(BankValues.ReferencesPeriod([], new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 31)));
    }
}
