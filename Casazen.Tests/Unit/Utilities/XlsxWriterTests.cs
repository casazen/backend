using System.IO.Compression;
using System.Xml.Linq;
using Casazen.Core.BankStatements;
using Casazen.Core.Exceptions;
using Casazen.Core.Utilities;
using Xunit;

namespace Casazen.Tests.Unit.Utilities;

/// <summary>
/// E06/F03/US03/T01 (FIS-07, NF-87): the Excel file the host downloads is a valid workbook made with nothing but the framework (the reader of the bank
/// statements opens it, every part is well-formed XML), keeps a number a number and a day a date, and never lets a spreadsheet run a text: a cell that
/// starts with <c>= + - @</c> is an inline string, with no formula element anywhere.
/// </summary>
public class XlsxWriterTests
{
    private static readonly XNamespace Main = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private static readonly string[] Header = ["Tipo", "Importo", "Data"];

    private static int Serial(int year, int month, int day) => (int)(new DateTime(year, month, day) - new DateTime(1899, 12, 30)).TotalDays;

    private static List<IReadOnlyList<object?>> Rows(params object?[][] rows) => [.. rows];

    private static Dictionary<string, XDocument> Parts(byte[] workbook)
    {
        using var zip = new ZipArchive(new MemoryStream(workbook, writable: false), ZipArchiveMode.Read);
        return zip.Entries.ToDictionary(
            entry => entry.FullName,
            entry =>
            {
                using var stream = entry.Open();
                return XDocument.Load(stream);
            });
    }

    [Fact]
    public void Write_TextStaysText_AmountsAndDaysAreNumbers_AndTheReaderOpensIt()
    {
        var workbook = XlsxWriter.Write(
            "Documenti",
            Header,
            Rows(
                ["Fattura", 12.5m, new DateOnly(2026, 10, 31)],
                ["=1+1", -3.25m, null],
                ["", 0m, new DateOnly(2000, 1, 1)]));

        Assert.True(XlsxTableReader.LooksLikeWorkbook(workbook));
        var table = XlsxTableReader.Read(workbook);

        Assert.Equal(4, table.Count);
        Assert.Equal(Header, table[0].Select(cell => cell.Text));
        Assert.Equal(new Cell("Fattura", false), table[1][0]);
        Assert.Equal(new Cell("12.50", true), table[1][1]);
        Assert.Equal(new Cell(Serial(2026, 10, 31).ToString(), true), table[1][2]);
        Assert.Equal(new Cell("=1+1", false), table[2][0]);
        Assert.Equal(new Cell("-3.25", true), table[2][1]);
        Assert.Equal(2, table[2].Length);
        Assert.Equal(Cell.Empty, table[3][0]);
        Assert.Equal(new Cell("0.00", true), table[3][1]);
        Assert.Equal(new Cell(Serial(2000, 1, 1).ToString(), true), table[3][2]);
    }

    /// <summary>Every part is well-formed XML and the package has the six parts of a workbook with one sheet, under the name that was asked.</summary>
    [Fact]
    public void Write_IsAWorkbookOfSixWellFormedParts_WithOneNamedSheet()
    {
        var parts = Parts(XlsxWriter.Write("Documenti", Header, Rows(["Fattura", 1m, null])));

        Assert.Equal(
            ["[Content_Types].xml", "_rels/.rels", "xl/_rels/workbook.xml.rels", "xl/styles.xml", "xl/workbook.xml", "xl/worksheets/sheet1.xml"],
            parts.Keys.Order(StringComparer.Ordinal));
        var sheet = Assert.Single(parts["xl/workbook.xml"].Descendants(Main + "sheet"));
        Assert.Equal("Documenti", (string?)sheet.Attribute("name"));
        Assert.Equal(3, parts["xl/worksheets/sheet1.xml"].Descendants(Main + "col").Count());
        Assert.NotNull(parts["xl/worksheets/sheet1.xml"].Descendants(Main + "pane").SingleOrDefault(pane => (string?)pane.Attribute("state") == "frozen"));
    }

    /// <summary>
    /// A formula is never run: the text is an inline string with the quote prefix of its style, the header is bold, an amount has the format 0.00 and a day the
    /// format dd/mm/yyyy, and there is no formula element at all.
    /// </summary>
    [Fact]
    public void Write_AFormulaIsAnInlineString_WithTheQuotePrefix_AndNoFormulaElementExists()
    {
        var parts = Parts(XlsxWriter.Write(
            "Documenti",
            Header,
            Rows(
                ["=1+1", 12.5m, new DateOnly(2026, 10, 31)],
                ["+39 333", 0m, null],
                ["-2+3", 0m, null],
                ["@SUM(A1)", 0m, null],
                ["Casa", 0m, null])));
        var sheet = parts["xl/worksheets/sheet1.xml"];
        var styles = parts["xl/styles.xml"];
        var formats = styles.Descendants(Main + "cellXfs").Single().Elements(Main + "xf").ToList();

        Assert.Empty(sheet.Descendants(Main + "f"));
        foreach (var reference in new[] { "A2", "A3", "A4", "A5" })
        {
            var cell = sheet.Descendants(Main + "c").Single(c => (string?)c.Attribute("r") == reference);
            Assert.Equal("inlineStr", (string?)cell.Attribute("t"));
            Assert.Equal("1", (string?)formats[int.Parse((string)cell.Attribute("s")!)].Attribute("quotePrefix"));
        }

        var plain = sheet.Descendants(Main + "c").Single(c => (string?)c.Attribute("r") == "A6");
        Assert.Null(plain.Attribute("s"));

        var header = sheet.Descendants(Main + "c").Single(c => (string?)c.Attribute("r") == "A1");
        var boldFont = styles.Descendants(Main + "fonts").Single().Elements(Main + "font").ElementAt(int.Parse((string)formats[int.Parse((string)header.Attribute("s")!)].Attribute("fontId")!));
        Assert.NotNull(boldFont.Element(Main + "b"));

        var amount = sheet.Descendants(Main + "c").Single(c => (string?)c.Attribute("r") == "B2");
        Assert.Equal("2", (string?)formats[int.Parse((string)amount.Attribute("s")!)].Attribute("numFmtId"));
        Assert.Equal("12.50", amount.Element(Main + "v")!.Value);

        var day = sheet.Descendants(Main + "c").Single(c => (string?)c.Attribute("r") == "C2");
        var dayFormat = (string)formats[int.Parse((string)day.Attribute("s")!)].Attribute("numFmtId")!;
        Assert.Equal("dd/mm/yyyy", styles.Descendants(Main + "numFmt").Single(f => (string?)f.Attribute("numFmtId") == dayFormat).Attribute("formatCode")!.Value);
    }

    [Fact]
    public void Write_RefusesMoreRowsThanAFileHolds_HeaderIncluded()
    {
        var two = Rows(["a", 1m, null], ["b", 2m, null]);

        Assert.NotEmpty(XlsxWriter.Write("S", Header, two, maxRows: 3));
        var refused = Assert.Throws<DomainRuleException>(() => XlsxWriter.Write("S", Header, two, maxRows: 2));
        Assert.Equal("xlsx_too_many_rows", refused.Code);
        Assert.Equal("CsvTooManyRows", refused.MessageKey);
        Assert.Equal(2, Assert.Single(refused.MessageArgs));
    }

    [Fact]
    public void Write_WithNoRows_IsTheHeaderAlone()
    {
        var table = XlsxTableReader.Read(XlsxWriter.Write("Documenti", Header, []));

        Assert.Equal(Header, Assert.Single(table).Select(cell => cell.Text));
    }

    /// <summary>A control code is no XML 1.0 character, a lone surrogate is no character at all: neither stops the file, a surrogate pair stays whole.</summary>
    [Fact]
    public void Write_LeavesOutWhatXmlDoesNotAllow_AndKeepsTheRest()
    {
        var table = XlsxTableReader.Read(XlsxWriter.Write(
            "S",
            Header,
            Rows(["a\u0001b\u0000c", "\U0001F600", "x\uD800y"], ["tab\tkept", "café €", "\uFFFEz"])));

        Assert.Equal(["abc", "\U0001F600", "xy"], table[1].Select(cell => cell.Text));
        Assert.Equal(["tab\tkept", "café €", "z"], table[2].Select(cell => cell.Text));
    }

    [Fact]
    public void Write_KeepsTheSpacesOfAText_AndCutsATextAtWhatACellHolds()
    {
        var parts = Parts(XlsxWriter.Write("S", Header, Rows(["  padded  ", new string('x', XlsxWriter.MaxCellTextLength + 500), "two\nlines"])));
        var cells = parts["xl/worksheets/sheet1.xml"].Descendants(Main + "c").Where(c => (string?)c.Attribute("t") == "inlineStr" && ((string)c.Attribute("r")!).EndsWith('2')).ToList();

        var padded = cells[0].Descendants(Main + "t").Single();
        Assert.Equal("preserve", (string?)padded.Attribute(XNamespace.Xml + "space"));
        Assert.Equal("  padded  ", padded.Value);
        Assert.InRange(cells[1].Descendants(Main + "t").Single().Value.Length, XlsxWriter.MaxCellTextLength - 1, XlsxWriter.MaxCellTextLength);
        Assert.Equal("two\nlines", cells[2].Descendants(Main + "t").Single().Value);
    }

    /// <summary>Before 1 March 1900 a date cell would say another day (Excel counts a 29 February 1900 that never was): the day is written as the text it is.</summary>
    [Fact]
    public void Write_ADayBeforeMarch1900_IsText_AndTheFirstDayOfTheCalendarIsASerial()
    {
        var table = XlsxTableReader.Read(XlsxWriter.Write("S", Header, Rows(["a", 1m, new DateOnly(1900, 2, 28)], ["b", 1m, new DateOnly(1900, 3, 1)])));

        Assert.Equal(new Cell("1900-02-28", false), table[1][2]);
        Assert.Equal(new Cell("61", true), table[2][2]);
        Assert.Null(XlsxWriter.SerialOf(new DateOnly(1899, 12, 31)));
        Assert.Equal(61, XlsxWriter.SerialOf(new DateOnly(1900, 3, 1)));
        Assert.Equal(Serial(2026, 11, 12), XlsxWriter.SerialOf(new DateOnly(2026, 11, 12)));
    }

    [Theory]
    [InlineData("Documenti", "Documenti")]
    [InlineData("A/B:C*D?[E]\\F", "ABCDEF")]
    [InlineData("'quoted'", "quoted")]
    [InlineData("", "Sheet1")]
    [InlineData(null, "Sheet1")]
    [InlineData("///", "Sheet1")]
    [InlineData("Un nome di foglio che e troppo lungo", "Un nome di foglio che e troppo")]
    public void SheetNameOf_IsWhatASheetMayBeCalled(string? name, string expected)
    {
        var sheetName = XlsxWriter.SheetNameOf(name);

        Assert.Equal(expected, sheetName);
        Assert.True(sheetName.Length <= XlsxWriter.MaxSheetNameLength);
    }

    [Theory]
    [InlineData(0, "A")]
    [InlineData(13, "N")]
    [InlineData(25, "Z")]
    [InlineData(26, "AA")]
    [InlineData(701, "ZZ")]
    [InlineData(702, "AAA")]
    public void ColumnName_CountsLikeASpreadsheet(int index, string expected) => Assert.Equal(expected, XlsxWriter.ColumnName(index));
}
