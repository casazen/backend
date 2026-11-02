using System.Globalization;
using System.IO.Compression;
using System.Security;
using System.Text;
using Casazen.Core.BankStatements;
using Casazen.Core.Exceptions;
using Xunit;

namespace Casazen.Tests.Unit.BankStatements;

/// <summary>
/// The first sheet of an Excel workbook, read with nothing but the framework: shared and inline strings, numbers stored as such, dates as
/// serials, cells left out; and the workbooks it refuses (not a workbook, a part that unzips to more than it may).
/// </summary>
public class BankStatementXlsxTests
{
    private static double Serial(int year, int month, int day) => (new DateTime(year, month, day) - new DateTime(1899, 12, 30)).TotalDays;

    /// <summary>Builds a minimal workbook: strings are shared strings (or inline when asked), numbers are numbers, null leaves the cell out.</summary>
    internal static byte[] Workbook(IReadOnlyList<object?[]> rows, bool inlineStrings = false)
    {
        var shared = new List<string>();
        var sheet = new StringBuilder("<?xml version=\"1.0\" encoding=\"UTF-8\"?><worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"><sheetData>");
        for (var r = 0; r < rows.Count; r++)
        {
            sheet.Append("<row r=\"").Append(r + 1).Append("\">");
            for (var c = 0; c < rows[r].Length; c++)
            {
                var value = rows[r][c];
                if (value is null)
                    continue;

                var reference = $"{(char)('A' + c)}{r + 1}";
                switch (value)
                {
                    case string text when inlineStrings:
                        sheet.Append("<c r=\"").Append(reference).Append("\" t=\"inlineStr\"><is><t>").Append(SecurityElement.Escape(text)).Append("</t></is></c>");
                        break;
                    case string text:
                        shared.Add(text);
                        sheet.Append("<c r=\"").Append(reference).Append("\" t=\"s\"><v>").Append(shared.Count - 1).Append("</v></c>");
                        break;
                    default:
                        sheet.Append("<c r=\"").Append(reference).Append("\"><v>").Append(Convert.ToString(value, CultureInfo.InvariantCulture)).Append("</v></c>");
                        break;
                }
            }

            sheet.Append("</row>");
        }

        sheet.Append("</sheetData></worksheet>");

        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            Add(zip, "[Content_Types].xml", "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"/>");
            Add(zip, "xl/workbook.xml", "<workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"><sheets><sheet name=\"Movimenti\" sheetId=\"1\" r:id=\"rId7\"/></sheets></workbook>");
            Add(zip, "xl/_rels/workbook.xml.rels", "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId7\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet3.xml\"/></Relationships>");
            Add(zip, "xl/worksheets/sheet3.xml", sheet.ToString());
            if (shared.Count > 0)
            {
                Add(zip, "xl/sharedStrings.xml", "<sst xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\">"
                    + string.Concat(shared.Select(s => $"<si><t>{SecurityElement.Escape(s)}</t></si>")) + "</sst>");
            }
        }

        return stream.ToArray();
    }

    private static void Add(ZipArchive zip, string name, string content)
    {
        var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
        using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
        writer.Write(content);
    }

    /// <summary>A workbook from the XML of its sheet (rows of <c>&lt;c&gt;</c>) and, if given, of its shared strings (the <c>&lt;si&gt;</c> items).</summary>
    private static byte[] RawWorkbook(string rows, string? sharedItems = null)
    {
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            Add(zip, "[Content_Types].xml", "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"/>");
            Add(zip, "xl/workbook.xml", "<workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"><sheets><sheet name=\"S\" sheetId=\"1\"/></sheets></workbook>");
            Add(zip, "xl/worksheets/sheet1.xml", "<worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"><sheetData>" + rows + "</sheetData></worksheet>");
            if (sharedItems is not null)
                Add(zip, "xl/sharedStrings.xml", "<sst xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\">" + sharedItems + "</sst>");
        }

        return stream.ToArray();
    }

    private const string HeaderRowInline =
        "<row r=\"1\"><c r=\"A1\" t=\"inlineStr\"><is><t>Data</t></is></c><c r=\"B1\" t=\"inlineStr\"><is><t>Descrizione</t></is></c><c r=\"C1\" t=\"inlineStr\"><is><t>Importo</t></is></c></row>";

    /// <summary>Runs the parser on a thread of its own and waits a while: a reader that never comes back must fail the test, not hold it.</summary>
    private static ParsedBankStatement ParseOrGiveUp(byte[] bytes)
    {
        var task = Task.Run(() => BankStatementParser.Parse(bytes));
        Assert.True(task.Wait(TimeSpan.FromSeconds(30)), "The reader did not come back from the workbook.");
        return task.Result;
    }

    [Fact]
    public void Xlsx_DatesAsSerialsAndAmountsAsNumbers_AreRead_WhateverTheNameOfTheSheetPart()
    {
        var bytes = Workbook(
        [
            ["Data contabile", "Data valuta", "Descrizione", "Importo"],
            [Serial(2026, 10, 3), Serial(2026, 10, 4), "Bonifico Verdi Giulia canone ottobre", 900.0],
            [Serial(2026, 10, 5), Serial(2026, 10, 5), "POS", -45.5],
        ]);

        var statement = BankStatementParser.Parse(bytes);

        Assert.Equal(BankStatementFormat.Xlsx, statement.Format);
        Assert.Equal(2, statement.MovementCount);
        var credit = Assert.Single(statement.Credits);
        Assert.Equal(new DateOnly(2026, 10, 3), credit.BookingDate);
        Assert.Equal(new DateOnly(2026, 10, 4), credit.ValueDate);
        Assert.Equal(900.00m, credit.Amount);
        Assert.Equal("Bonifico Verdi Giulia canone ottobre", credit.Description);
    }

    [Fact]
    public void Xlsx_DatesAndAmountsWrittenAsText_AreReadAsInACsv()
    {
        var bytes = Workbook(
        [
            ["Data", "Causale", "Dare", "Avere"],
            ["03/10/2026", "Canone", null, "1.250,00"],
            ["04/10/2026", "Spesa", "20,00", null],
        ]);

        var statement = BankStatementParser.Parse(bytes);

        Assert.Equal(1250.00m, Assert.Single(statement.Credits).Amount);
        Assert.Equal(2, statement.MovementCount);
    }

    [Fact]
    public void Xlsx_InlineStrings_AndCellsLeftOut_AreRead()
    {
        var bytes = Workbook(
            [
                ["Data", "Descrizione", "Ordinante", "Importo"],
                ["03/10/2026", "Canone", null, 900.0],
                ["04/10/2026", "Canone ottobre", "Giulia Verdi", 800.5],
            ],
            inlineStrings: true);

        var statement = BankStatementParser.Parse(bytes);

        Assert.Equal(2, statement.Credits.Count);
        Assert.Null(statement.Credits[0].CounterpartyName);
        Assert.Equal("Giulia Verdi", statement.Credits[1].CounterpartyName);
        Assert.Equal(800.50m, statement.Credits[1].Amount);
    }

    [Fact]
    public void Xlsx_AnEmptyInlineString_IsAnEmptyCell_AndTheReaderMovesOn()
    {
        // «<is/>» has no end tag to stop on: a reader that read it again from the same place would never come back.
        var date = Serial(2026, 10, 3).ToString(CultureInfo.InvariantCulture);
        var bytes = RawWorkbook(
            HeaderRowInline
            + $"<row r=\"2\"><c r=\"A2\"><v>{date}</v></c><c r=\"B2\" t=\"inlineStr\"><is/></c><c r=\"C2\"><v>900</v></c></row>"
            + $"<row r=\"3\"><c r=\"A3\"><is/></c><c r=\"B3\" t=\"inlineStr\"><is><t>Altro</t></is></c><c r=\"C3\"><v>12</v></c></row>"
            + $"<row r=\"4\"><c r=\"A4\"><v>{date}</v></c><c r=\"B4\" t=\"inlineStr\"><is></is></c><c r=\"C4\"><v>800</v></c></row>");

        var statement = ParseOrGiveUp(bytes);

        Assert.Equal(new[] { 900.00m, 800.00m }, statement.Credits.Select(c => c.Amount).ToArray());
        Assert.All(statement.Credits, credit => Assert.Equal(string.Empty, credit.Description));
    }

    [Fact]
    public void Xlsx_ASharedStringInRuns_IsJoined_AndItsPhoneticHintsAreLeftOut()
    {
        var date = Serial(2026, 10, 3).ToString(CultureInfo.InvariantCulture);
        var bytes = RawWorkbook(
            "<row r=\"1\"><c r=\"A1\" t=\"s\"><v>0</v></c><c r=\"B1\" t=\"s\"><v>1</v></c><c r=\"C1\" t=\"s\"><v>2</v></c></row>"
            + $"<row r=\"2\"><c r=\"A2\"><v>{date}</v></c><c r=\"B2\" t=\"s\"><v>3</v></c><c r=\"C2\"><v>900</v></c></row>",
            "<si><t>Data</t></si><si><t>Descrizione</t></si><si><t>Importo</t></si>"
            + "<si><r><rPr><b/></rPr><t xml:space=\"preserve\">Bonifico </t></r><rPh sb=\"0\" eb=\"1\"><t>IGNORATO</t></rPh><r><t>Verdi</t></r><t/><rPh sb=\"1\" eb=\"2\"/></si>");

        var credit = Assert.Single(ParseOrGiveUp(bytes).Credits);

        Assert.Equal("Bonifico Verdi", credit.Description);
    }

    [Fact]
    public void Xlsx_ASharedStringOfMillionsOfEmptyRuns_IsReadNodeByNode_NotAsATree()
    {
        var date = Serial(2026, 10, 3).ToString(CultureInfo.InvariantCulture);
        var runs = string.Concat(Enumerable.Repeat("<r/>", 3_000_000));
        var bytes = RawWorkbook(
            "<row r=\"1\"><c r=\"A1\" t=\"s\"><v>0</v></c><c r=\"B1\" t=\"s\"><v>1</v></c><c r=\"C1\" t=\"s\"><v>2</v></c></row>"
            + $"<row r=\"2\"><c r=\"A2\"><v>{date}</v></c><c r=\"B2\" t=\"s\"><v>3</v></c><c r=\"C2\"><v>900</v></c></row>",
            $"<si><t>Data</t>{runs}</si><si><t>Descrizione</t></si><si><t>Importo</t></si><si><t>Canone</t></si>");

        var before = GC.GetAllocatedBytesForCurrentThread();
        var statement = BankStatementParser.Parse(bytes);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal("Canone", Assert.Single(statement.Credits).Description);
        // As an XElement tree these runs would cost hundreds of MB; read as nodes they cost next to nothing.
        Assert.True(allocated < 64L * 1024 * 1024, $"The shared strings allocated {allocated / (1024 * 1024)} MB.");
    }

    [Fact]
    public void Xlsx_AWorkbookWithoutTheKnownColumns_AsksForAMapping()
    {
        var bytes = Workbook(
        [
            ["Foo", "Bar", "Baz"],
            [Serial(2026, 10, 3), "Canone", 900.0],
        ]);

        var ex = Assert.Throws<DomainRuleException>(() => BankStatementParser.Parse(bytes));
        Assert.Equal(BankStatementErrorCodes.ColumnsUnrecognized, ex.Code);

        var statement = BankStatementParser.Parse(bytes, new BankColumnMapping(HeaderRow: 0, Date: 0, Description: 1, Amount: 2));
        Assert.Equal(900.00m, Assert.Single(statement.Credits).Amount);
    }

    [Fact]
    public void Xlsx_ASheetPartThatUnzipsToMoreThanTheLimit_IsRefused()
    {
        // A workbook whose sheet is one cell of 48 MB and a little more of the same letter: a few dozen KB zipped, too large once unzipped.
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            Add(zip, "xl/workbook.xml", "<workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"><sheets><sheet name=\"S\" sheetId=\"1\"/></sheets></workbook>");
            using var writer = new StreamWriter(zip.CreateEntry("xl/worksheets/sheet1.xml", CompressionLevel.Optimal).Open(), new UTF8Encoding(false));
            writer.Write("<worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"><sheetData><row r=\"1\"><c r=\"A1\" t=\"inlineStr\"><is><t>");
            var chunk = new string('a', 1024);
            for (long written = 0; written <= BankStatementLimits.MaxUnzippedPartBytes + 4096; written += chunk.Length)
                writer.Write(chunk);
            writer.Write("</t></is></c></row></sheetData></worksheet>");
        }

        var bytes = stream.ToArray();

        // The file itself is small: it is the part that is large once unzipped.
        Assert.True(bytes.Length < BankStatementLimits.MaxFileBytes);
        var ex = Assert.Throws<DomainRuleException>(() => BankStatementParser.Parse(bytes));

        Assert.Equal(BankStatementErrorCodes.Unreadable, ex.Code);
    }

    [Fact]
    public void Xlsx_AZipThatIsNotAWorkbook_IsNotAStatement()
    {
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
            Add(zip, "movimenti.csv", "Data;Descrizione;Importo\n03/10/2026;Canone;900,00\n");

        var ex = Assert.Throws<DomainUnsupportedMediaTypeException>(() => BankStatementParser.Parse(stream.ToArray()));

        Assert.Equal(BankStatementErrorCodes.FormatUnsupported, ex.Code);
    }

    [Fact]
    public void Xlsx_ADamagedZip_IsNotAStatement()
    {
        byte[] damaged = [(byte)'P', (byte)'K', 3, 4, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12];

        Assert.Throws<DomainUnsupportedMediaTypeException>(() => BankStatementParser.Parse(damaged));
    }

    [Theory]
    [InlineData("A1", 0)]
    [InlineData("C12", 2)]
    [InlineData("Z9", 25)]
    [InlineData("AA1", 26)]
    [InlineData("AB10", 27)]
    public void ColumnIndex_OfACellReference(string reference, int expected)
    {
        Assert.Equal(expected, XlsxTableReader.ColumnIndex(reference));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("12")]
    [InlineData("ABCD1")]
    public void ColumnIndex_OfAnOddReference_IsNone(string? reference)
    {
        Assert.Null(XlsxTableReader.ColumnIndex(reference));
    }
}
