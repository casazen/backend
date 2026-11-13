using System.Globalization;
using System.Text;
using Casazen.Core.Exceptions;
using Casazen.Core.Utilities;
using Xunit;

namespace Casazen.Tests.Unit.Utilities;

/// <summary>
/// E07/F03/US05/T01 (REP-06, NF-101): a report is one <see cref="ReportTable"/>, and the CSV written from it is what the host reports have always written:
/// the title row, the header and the rows, a percentage as the percentage, a day as <c>dd/MM/yyyy</c>, an amount to the cent with the decimal comma, a text a
/// spreadsheet would run as a formula with a leading apostrophe (never a number: a negative amount stays a number).
/// </summary>
public class ReportTableTests
{
    private const string Bom = "﻿";

    private static string Text(byte[] file) => Encoding.UTF8.GetString(file);

    [Fact]
    public void ReportCell_RoundsOnce_ToThePlacesTheFilesShow()
    {
        Assert.Equal(1.01m, ReportCell.Money(1.005m).DecimalValue);
        Assert.Equal(-1.01m, ReportCell.Money(-1.005m).DecimalValue);
        Assert.Equal(12.35m, ReportCell.Number(12.345m).DecimalValue);
        Assert.Equal(0.4556m, ReportCell.Percent(0.45555m).DecimalValue);
        Assert.Equal(ReportCellKind.Decimal, ReportCell.Money(1m).Kind);
        Assert.Equal(ReportCellKind.Percent, ReportCell.Percent(0m).Kind);
    }

    [Fact]
    public void ReportCell_NothingIsAnEmptyCell_NeverAZero()
    {
        Assert.Equal(ReportCell.Empty, ReportCell.Money(null));
        Assert.Equal(ReportCell.Empty, ReportCell.Number(null));
        Assert.Equal(ReportCell.Empty, ReportCell.Percent(null));
        Assert.Equal(ReportCell.Empty, ReportCell.Integer(null));
        Assert.Equal(ReportCell.Empty, ReportCell.Date(null));
        Assert.Equal(ReportCell.Empty, ReportCell.Text(null));
        Assert.Equal(ReportCell.Empty, ReportCell.Text(string.Empty));
        Assert.Equal(ReportCellKind.Decimal, ReportCell.Money(0m).Kind);
        Assert.Equal(ReportCellKind.Empty, default(ReportCell).Kind);
    }

    [Fact]
    public void Csv_OfTwoTables_IsTitleHeaderAndRowsOfEach_WithAnEmptyRowBetween()
    {
        var first = new ReportTable("Incassato, per cassa, dal 01/10/2026 al 31/10/2026", ["Immobile", "Incassato (€)", "Soggiorni", "Quota (%)", "Dal"]);
        first.Add("Casa Bianca", ReportCell.Money(1234.5m), 3, ReportCell.Percent(0.455m), ReportCell.Date(new DateOnly(2026, 10, 5)));
        first.Add("Totale", ReportCell.Empty, 5, ReportCell.Percent(1m), ReportCell.Empty);
        var second = new ReportTable("Secondo", ["A", "B"]);
        second.Add("x", ReportCell.Integer(1));

        var file = Text(CsvWriter.Write([first, second]));

        Assert.Equal(
            Bom
            + "Incassato, per cassa, dal 01/10/2026 al 31/10/2026\r\n"
            + "Immobile;Incassato (€);Soggiorni;Quota (%);Dal\r\n"
            + "Casa Bianca;1234,50;3;45,50;05/10/2026\r\n"
            + "Totale;;5;100,00;\r\n"
            + "\r\n"
            + "Secondo\r\n"
            + "A;B\r\n"
            + "x;1\r\n",
            file);
    }

    [Fact]
    public void Csv_OfOneTable_IsTheSameAsTheListOfThatTable()
    {
        var table = new ReportTable("T", ["A"]);
        table.Add("x");

        Assert.Equal(CsvWriter.Write([table]), CsvWriter.Write(table));
    }

    /// <summary>A text a spreadsheet would run (<c>= + - @</c>, a tab or a CR first) gets the apostrophe; an amount never does, a negative one included.</summary>
    [Fact]
    public void Csv_AFormulaLookingText_GetsALeadingApostrophe_AndANegativeAmountDoesNot()
    {
        var table = new ReportTable("=1+1 title", ["@x"]);
        table.Add("=1+1", ReportCell.Money(-3.25m), "+39 333");
        table.Add("-2+3", "@SUM(A1)", "\tcmd");
        table.Add("plain", ReportCell.Integer(-4));

        var lines = Text(CsvWriter.Write(table)).TrimStart('﻿').Split("\r\n");

        Assert.Equal("'=1+1 title", lines[0]);
        Assert.Equal("'@x", lines[1]);
        Assert.Equal("'=1+1;-3,25;'+39 333", lines[2]);
        Assert.Equal("'-2+3;'@SUM(A1);'\tcmd", lines[3]);
        Assert.Equal("plain;-4", lines[4]);

        // A CR is a line break to a CSV reader: the cell is quoted, and the apostrophe is inside the quotes.
        Assert.Equal("\"'\rcr\"", CsvWriter.Text("\rcr"));
    }

    /// <summary>The day is formatted with the date specifiers of <see cref="DateOnly"/> only (a time specifier throws): real values, the edges of the calendar too.</summary>
    [Theory]
    [InlineData(2026, 1, 9, "09/01/2026")]
    [InlineData(2026, 10, 31, "31/10/2026")]
    [InlineData(2028, 2, 29, "29/02/2028")]
    [InlineData(1, 1, 1, "01/01/0001")]
    [InlineData(9999, 12, 31, "31/12/9999")]
    public void Csv_ADay_IsWrittenAsDdMmYyyy_WhateverTheCultureOfTheServer(int year, int month, int day, string expected)
    {
        var previous = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("ar-SA");
        try
        {
            var table = new ReportTable("T", ["Giorno"]);
            table.Add(ReportCell.Date(new DateOnly(year, month, day)));

            Assert.Equal(expected, Text(CsvWriter.Write(table)).Split("\r\n")[2]);
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Fact]
    public void Csv_AnAmount_IsWrittenToTheCentWithTheDecimalComma_AndNoThousandsSeparator()
    {
        var table = new ReportTable("T", ["Importo"]);
        table.Add(ReportCell.Money(1234567.891m));
        table.Add(ReportCell.Money(0.1m + 0.2m));
        table.Add(ReportCell.Money(-0.004m));

        var lines = Text(CsvWriter.Write(table)).Split("\r\n");

        Assert.Equal("1234567,89", lines[2]);
        Assert.Equal("0,30", lines[3]);
        Assert.Equal("0,00", lines[4]);
    }

    [Fact]
    public void Csv_RefusesMoreRowsThanAFileHolds_TitleAndHeaderIncluded()
    {
        var table = new ReportTable("T", ["A"]);
        table.Add("1");
        table.Add("2");

        Assert.Equal(4, table.RowCount);
        Assert.NotEmpty(CsvWriter.Write(table, maxRows: 4));
        var refused = Assert.Throws<DomainRuleException>(() => CsvWriter.Write(table, maxRows: 3));
        Assert.Equal("csv_too_many_rows", refused.Code);
        Assert.Equal(3, Assert.Single(refused.MessageArgs));
    }
}
