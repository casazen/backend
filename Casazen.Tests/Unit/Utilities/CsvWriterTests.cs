using System.Globalization;
using System.Text;
using Casazen.Core.Utilities;
using Xunit;

namespace Casazen.Tests.Unit.Utilities;

public class CsvWriterTests
{
    [Theory]
    [InlineData("=1+1", "'=1+1")]
    [InlineData("+39 333 1234567", "'+39 333 1234567")]
    [InlineData("-2+3", "'-2+3")]
    [InlineData("@SUM(A1)", "'@SUM(A1)")]
    [InlineData("\tcmd", "'\tcmd")]
    [InlineData("\rcmd", "\"'\rcmd\"")]
    [InlineData("=HYPERLINK(\"http://evil\";\"x\")", "\"'=HYPERLINK(\"\"http://evil\"\";\"\"x\"\")\"")]
    [InlineData("Trullo di Ostuni", "Trullo di Ostuni")]
    public void Write_AFormulaIsText_AndANumberIsANumber_InAnItalianFile(string name, string expectedCell)
    {
        var rows = new List<IReadOnlyList<object?>> { new object?[] { name, 12.5m, -3 } };

        var file = CsvWriter.Write(rows);

        Assert.Equal(Encoding.UTF8.GetPreamble(), file[..3]);
        Assert.Equal(expectedCell + ";12,50;-3\r\n", Encoding.UTF8.GetString(file, 3, file.Length - 3));
    }

    [Fact]
    public void Write_ACalendarDay_IsWrittenAsAnIsoDay_WhateverTheCultureOfTheServer()
    {
        var culture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("ar-SA");
            var file = CsvWriter.Write([new object?[] { new DateOnly(2026, 11, 2), null, "x" }]);

            Assert.Equal("2026-11-02;;x\r\n", Encoding.UTF8.GetString(file, 3, file.Length - 3));
        }
        finally
        {
            CultureInfo.CurrentCulture = culture;
        }
    }

    [Theory]
    [InlineData("=1+1", true)]
    [InlineData("+39 333", true)]
    [InlineData("-2", true)]
    [InlineData("@SUM(A1)", true)]
    [InlineData("\tcmd", true)]
    [InlineData("\rcmd", true)]
    [InlineData("1+1", false)]
    [InlineData("Casa =1", false)]
    [InlineData("", false)]
    public void StartsLikeAFormula_IsTheRuleOfEveryFileTheHostDownloads(string text, bool expected)
    {
        Assert.Equal(expected, CsvWriter.StartsLikeAFormula(text));
        Assert.Equal(expected, CsvWriter.Text(text).StartsWith('\'') || CsvWriter.Text(text).StartsWith("\"'", StringComparison.Ordinal));
    }
}
