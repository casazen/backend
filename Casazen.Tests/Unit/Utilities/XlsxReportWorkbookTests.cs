using System.IO.Compression;
using System.Xml.Linq;
using Casazen.Core.BankStatements;
using Casazen.Core.Exceptions;
using Casazen.Core.Utilities;
using Xunit;

namespace Casazen.Tests.Unit.Utilities;

/// <summary>
/// E07/F03/US05/T01 (REP-06, NF-101): the workbook of a report is valid OOXML made with nothing but the framework (the reader of the bank statements opens it,
/// every part is well-formed XML), has a sheet for each table with its title row and its header kept on top, keeps a number a number with its format
/// (<c>#,##0.00</c>, <c>0.00%</c>), a day a date and an amount the exact decimal text of its value, and never lets a spreadsheet run a text: a cell that starts
/// with <c>= + - @</c>, a tab or a CR is an inline string with the quote prefix, in the title and the header as in the rows, with no formula element anywhere.
/// </summary>
public class XlsxReportWorkbookTests
{
    private static readonly XNamespace Main = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private static readonly XNamespace Rels = "http://schemas.openxmlformats.org/package/2006/relationships";
    private static readonly XNamespace Types = "http://schemas.openxmlformats.org/package/2006/content-types";

    private static int Serial(int year, int month, int day) => (int)(new DateTime(year, month, day) - new DateTime(1899, 12, 30)).TotalDays;

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

    private static XElement? CellAt(XDocument sheet, string reference) =>
        sheet.Descendants(Main + "c").SingleOrDefault(c => (string?)c.Attribute("r") == reference);

    private static XElement Style(XDocument styles, XElement cell)
    {
        var position = (string?)cell.Attribute("s") ?? "0";
        return styles.Descendants(Main + "cellXfs").Single().Elements(Main + "xf").ElementAt(int.Parse(position));
    }

    /// <summary>The format code of a cell: the custom one its style points to, <c>General</c> for the default style.</summary>
    private static string FormatCode(XDocument styles, XElement cell)
    {
        var id = (string)Style(styles, cell).Attribute("numFmtId")!;
        if (id == "0")
            return "General";

        return (string?)styles.Descendants(Main + "numFmt").Single(f => (string?)f.Attribute("numFmtId") == id).Attribute("formatCode") ?? "?";
    }

    private static bool IsBold(XDocument styles, XElement cell)
    {
        var font = styles.Descendants(Main + "fonts").Single().Elements(Main + "font").ElementAt(int.Parse((string)Style(styles, cell).Attribute("fontId")!));
        return font.Element(Main + "b") is not null;
    }

    private static bool HasQuotePrefix(XDocument styles, XElement cell) => (string?)Style(styles, cell).Attribute("quotePrefix") == "1";

    private static ReportSheet Sheet(string name, ReportTable table) => new(name, table);

    private static ReportTable Sample()
    {
        var table = new ReportTable("Incassato, per cassa, dal 01/10/2026 al 31/10/2026", ["Immobile", "Importo", "Soggiorni", "Quota", "Giorno", "Notti", "Vuoto"]);
        table.Add("Casa", ReportCell.Money(1234.5m), 7, ReportCell.Percent(0.455m), ReportCell.Date(new DateOnly(2026, 10, 31)), ReportCell.Number(12.5m), ReportCell.Empty);
        return table;
    }

    [Fact]
    public void Write_TwoSheets_IsAWorkbookOfWellFormedParts_WithOneSheetPartEach_AndTheReaderOpensIt()
    {
        var other = new ReportTable("Secondo", ["A"]);
        other.Add("x");
        var workbook = XlsxWriter.Write([Sheet("Primo", Sample()), Sheet("Secondo", other)]);

        Assert.True(XlsxTableReader.LooksLikeWorkbook(workbook));
        var parts = Parts(workbook);
        Assert.Equal(
            [
                "[Content_Types].xml",
                "_rels/.rels",
                "xl/_rels/workbook.xml.rels",
                "xl/styles.xml",
                "xl/workbook.xml",
                "xl/worksheets/sheet1.xml",
                "xl/worksheets/sheet2.xml",
            ],
            parts.Keys.Order(StringComparer.Ordinal));

        var sheets = parts["xl/workbook.xml"].Descendants(Main + "sheet").ToList();
        Assert.Equal(["Primo", "Secondo"], sheets.Select(s => (string?)s.Attribute("name")));
        Assert.Equal(["1", "2"], sheets.Select(s => (string?)s.Attribute("sheetId")));
        var relationships = parts["xl/_rels/workbook.xml.rels"].Descendants(Rels + "Relationship").ToDictionary(r => (string)r.Attribute("Id")!, r => (string)r.Attribute("Target")!);
        Assert.Equal("worksheets/sheet1.xml", relationships["rId1"]);
        Assert.Equal("worksheets/sheet2.xml", relationships["rId2"]);
        Assert.Equal("styles.xml", relationships["rId3"]);
        var overrides = parts["[Content_Types].xml"].Descendants(Types + "Override").Select(o => (string?)o.Attribute("PartName")).ToList();
        Assert.Contains("/xl/worksheets/sheet1.xml", overrides);
        Assert.Contains("/xl/worksheets/sheet2.xml", overrides);

        // The reader of the bank statements reads the first sheet: the title row, the header, then the row, numbers as numbers.
        var table = XlsxTableReader.Read(workbook);
        Assert.Equal(3, table.Count);
        Assert.Equal(new Cell("Incassato, per cassa, dal 01/10/2026 al 31/10/2026", false), Assert.Single(table[0]));
        Assert.Equal(["Immobile", "Importo", "Soggiorni", "Quota", "Giorno", "Notti", "Vuoto"], table[1].Select(cell => cell.Text));
        Assert.Equal(new Cell("Casa", false), table[2][0]);
        Assert.Equal(new Cell("1234.50", true), table[2][1]);
        Assert.Equal(new Cell("7", true), table[2][2]);
        Assert.Equal(new Cell("0.455", true), table[2][3]);
        Assert.Equal(new Cell(Serial(2026, 10, 31).ToString(), true), table[2][4]);
        Assert.Equal(new Cell("12.50", true), table[2][5]);
    }

    /// <summary>Numbers are number cells with their format (an amount <c>#,##0.00</c>, a percentage the fraction with <c>0.00%</c>, a day <c>dd/mm/yyyy</c>), never text.</summary>
    [Fact]
    public void Write_NumbersAreNumberCellsWithTheirFormats_AndADayIsADateSerial()
    {
        var parts = Parts(XlsxWriter.Write([Sheet("Foglio", Sample())]));
        var sheet = parts["xl/worksheets/sheet1.xml"];
        var styles = parts["xl/styles.xml"];

        var formatCodes = styles.Descendants(Main + "numFmt").Select(f => (string?)f.Attribute("formatCode")).ToList();
        Assert.Contains("#,##0.00", formatCodes);
        Assert.Contains("0.00%", formatCodes);
        Assert.Contains("dd/mm/yyyy", formatCodes);

        var text = CellAt(sheet, "A3")!;
        Assert.Equal("inlineStr", (string?)text.Attribute("t"));
        Assert.Equal("Casa", text.Descendants(Main + "t").Single().Value);

        var amount = CellAt(sheet, "B3")!;
        Assert.Null(amount.Attribute("t"));
        Assert.Equal("1234.50", amount.Element(Main + "v")!.Value);
        Assert.Equal("#,##0.00", FormatCode(styles, amount));

        var count = CellAt(sheet, "C3")!;
        Assert.Null(count.Attribute("t"));
        Assert.Equal("7", count.Element(Main + "v")!.Value);
        Assert.Equal("General", FormatCode(styles, count));

        var share = CellAt(sheet, "D3")!;
        Assert.Equal("0.455", share.Element(Main + "v")!.Value);
        Assert.Equal("0.00%", FormatCode(styles, share));

        var day = CellAt(sheet, "E3")!;
        Assert.Equal(Serial(2026, 10, 31).ToString(), day.Element(Main + "v")!.Value);
        Assert.Equal("dd/mm/yyyy", FormatCode(styles, day));

        var nights = CellAt(sheet, "F3")!;
        Assert.Equal("12.50", nights.Element(Main + "v")!.Value);
        Assert.Equal("#,##0.00", FormatCode(styles, nights));

        Assert.Null(CellAt(sheet, "G3"));
    }

    [Fact]
    public void Write_TheTitleAndTheHeaderAreBoldAndStayOnTop_AndTheTitleDoesNotWidenTheFirstColumn()
    {
        var table = new ReportTable(new string('T', 200), ["Immobile", "Importo"]);
        table.Add("Casa", ReportCell.Money(1m));
        var parts = Parts(XlsxWriter.Write([Sheet("Foglio", table)]));
        var sheet = parts["xl/worksheets/sheet1.xml"];
        var styles = parts["xl/styles.xml"];

        var pane = sheet.Descendants(Main + "pane").Single();
        Assert.Equal("frozen", (string?)pane.Attribute("state"));
        Assert.Equal("2", (string?)pane.Attribute("ySplit"));
        Assert.Equal("A3", (string?)pane.Attribute("topLeftCell"));

        Assert.True(IsBold(styles, CellAt(sheet, "A1")!));
        Assert.True(IsBold(styles, CellAt(sheet, "A2")!));
        Assert.True(IsBold(styles, CellAt(sheet, "B2")!));
        Assert.False(IsBold(styles, CellAt(sheet, "A3")!));

        // The title runs over the empty cells beside it: it does not make the first column as wide as the title.
        var widths = sheet.Descendants(Main + "col").Select(c => double.Parse((string)c.Attribute("width")!, System.Globalization.CultureInfo.InvariantCulture)).ToList();
        Assert.Equal(2, widths.Count);
        Assert.All(widths, width => Assert.InRange(width, 10, 20));
    }

    /// <summary>
    /// A text that a spreadsheet would run is an inline string with the quote prefix in its style, in the title, the header and the rows alike; there is no formula
    /// element in any sheet, and a negative amount is a number cell, not a text.
    /// </summary>
    [Fact]
    public void Write_ATextThatLooksLikeAFormula_IsNeverAFormula_AndANegativeAmountIsStillANumber()
    {
        var table = new ReportTable("=cmd|' /C calc'!A0", ["@Nome", "+Importo"]);
        table.Add("=1+1", ReportCell.Money(-3.25m));
        table.Add("+39 333", ReportCell.Integer(-1));
        table.Add("-2+3", ReportCell.Empty);
        table.Add("@SUM(A1)", ReportCell.Empty);
        table.Add("\tTab", ReportCell.Empty);
        table.Add("\rCr", ReportCell.Empty);
        table.Add("\u0001=1+1", ReportCell.Empty);
        table.Add("Normale", ReportCell.Empty);
        var workbook = XlsxWriter.Write([Sheet("Foglio", table)]);
        var parts = Parts(workbook);
        var sheet = parts["xl/worksheets/sheet1.xml"];
        var styles = parts["xl/styles.xml"];

        Assert.Empty(sheet.Descendants(Main + "f"));
        foreach (var reference in new[] { "A1", "A2", "B2", "A3", "A4", "A5", "A6", "A7", "A8", "A9" })
        {
            var cell = CellAt(sheet, reference)!;
            Assert.Equal("inlineStr", (string?)cell.Attribute("t"));
            Assert.True(HasQuotePrefix(styles, cell), reference);
        }

        var plain = CellAt(sheet, "A10")!;
        Assert.False(HasQuotePrefix(styles, plain));

        var negative = CellAt(sheet, "B3")!;
        Assert.Null(negative.Attribute("t"));
        Assert.Equal("-3.25", negative.Element(Main + "v")!.Value);
        Assert.Equal("#,##0.00", FormatCode(styles, negative));
        Assert.False(HasQuotePrefix(styles, negative));
        Assert.Equal("-1", CellAt(sheet, "B4")!.Element(Main + "v")!.Value);

        var read = XlsxTableReader.Read(workbook);
        Assert.Equal(new Cell("=1+1", false), read[2][0]);
        Assert.Equal(new Cell("-3.25", true), read[2][1]);
        Assert.Equal(new Cell("@SUM(A1)", false), read[5][0]);
    }

    /// <summary>The numbers are the exact decimal text of the value: no floating point, no exponent, rounded once to the places the file shows.</summary>
    [Fact]
    public void Write_AmountsAreTheExactDecimalText_NeverAFloat()
    {
        var table = new ReportTable("T", ["Importo"]);
        table.Add(ReportCell.Money(0.1m + 0.2m));
        table.Add(ReportCell.Money(12345678901234.567m));
        table.Add(ReportCell.Percent(1m / 3m));
        table.Add(ReportCell.Money(-0.005m));
        table.Add(ReportCell.Money(0m));
        table.Add(ReportCell.Percent(1m));
        table.Add(ReportCell.Percent(0.0001m));
        var sheet = Parts(XlsxWriter.Write([Sheet("S", table)]))["xl/worksheets/sheet1.xml"];

        var values = Enumerable.Range(3, 7).Select(row => CellAt(sheet, "A" + row)!.Element(Main + "v")!.Value).ToList();

        Assert.Equal(["0.30", "12345678901234.57", "0.3333", "-0.01", "0.00", "1", "0.0001"], values);
        Assert.All(values, value => Assert.DoesNotContain('E', value));
    }

    /// <summary>A day is a date serial with the date format, worked out from the real calendar; before 1 March 1900 it is the text of the day.</summary>
    [Fact]
    public void Write_ADay_IsADateCell_AndADayBeforeMarch1900IsText()
    {
        var table = new ReportTable("T", ["Giorno"]);
        table.Add(ReportCell.Date(new DateOnly(2026, 10, 31)));
        table.Add(ReportCell.Date(new DateOnly(2028, 2, 29)));
        table.Add(ReportCell.Date(new DateOnly(1900, 3, 1)));
        table.Add(ReportCell.Date(new DateOnly(1900, 2, 28)));
        var parts = Parts(XlsxWriter.Write([Sheet("S", table)]));
        var sheet = parts["xl/worksheets/sheet1.xml"];
        var styles = parts["xl/styles.xml"];

        Assert.Equal(Serial(2026, 10, 31).ToString(), CellAt(sheet, "A3")!.Element(Main + "v")!.Value);
        Assert.Equal(Serial(2028, 2, 29).ToString(), CellAt(sheet, "A4")!.Element(Main + "v")!.Value);
        Assert.Equal("61", CellAt(sheet, "A5")!.Element(Main + "v")!.Value);
        Assert.Equal("dd/mm/yyyy", FormatCode(styles, CellAt(sheet, "A3")!));

        var early = CellAt(sheet, "A6")!;
        Assert.Equal("inlineStr", (string?)early.Attribute("t"));
        Assert.Equal("1900-02-28", early.Descendants(Main + "t").Single().Value);
    }

    [Fact]
    public void Write_ASheetBeyondTheRowLimit_StopsTheFile_AndOneAtTheLimitIsWritten()
    {
        var two = new ReportTable("T", ["A"]);
        two.Add("1");
        two.Add("2");
        var one = new ReportTable("U", ["A"]);
        one.Add("1");

        Assert.NotEmpty(XlsxWriter.Write([Sheet("S", two)], maxRows: 4));

        var refused = Assert.Throws<DomainRuleException>(() => XlsxWriter.Write([Sheet("S", one), Sheet("T", two)], maxRows: 3));
        Assert.Equal("xlsx_too_many_rows", refused.Code);
        Assert.Equal("CsvTooManyRows", refused.MessageKey);
        Assert.Equal(3, Assert.Single(refused.MessageArgs));
    }

    [Fact]
    public void Write_ATableWithNoRows_IsTheTitleAndTheHeader()
    {
        var table = XlsxTableReader.Read(XlsxWriter.Write([Sheet("S", new ReportTable("Titolo", ["A", "B"]))]));

        Assert.Equal(2, table.Count);
        Assert.Equal(new Cell("Titolo", false), Assert.Single(table[0]));
        Assert.Equal(["A", "B"], table[1].Select(cell => cell.Text));
    }

    [Fact]
    public void Write_WithNoSheet_IsRefused()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => XlsxWriter.Write(new List<ReportSheet>()));
    }

    /// <summary>A name a workbook would refuse is cleaned (31 characters at most, none of <c>[ ] : * ? / \</c>) and two the same, whatever the case, are told apart.</summary>
    [Fact]
    public void SheetNamesOf_AreCleaned_AndUniqueWhateverTheCase()
    {
        var names = XlsxWriter.SheetNamesOf(
        [
            Sheet("Incassato per data", new ReportTable("T", [])),
            Sheet("incassato PER data", new ReportTable("T", [])),
            Sheet("Incassato per data", new ReportTable("T", [])),
            Sheet("A/B:C*D?[E]\\F", new ReportTable("T", [])),
            Sheet(string.Empty, new ReportTable("T", [])),
            Sheet("Un nome di foglio che e troppo lungo per un foglio", new ReportTable("T", [])),
            Sheet("Un nome di foglio che e troppo lungo per un foglio", new ReportTable("T", [])),
        ]);

        Assert.Equal(["Incassato per data", "incassato PER data (2)", "Incassato per data (3)", "ABCDEF", "Sheet1"], names.Take(5));
        Assert.All(names, name => Assert.InRange(name.Length, 1, XlsxWriter.MaxSheetNameLength));
        Assert.Equal(names.Count, names.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.EndsWith(" (2)", names[6], StringComparison.Ordinal);
    }
}
