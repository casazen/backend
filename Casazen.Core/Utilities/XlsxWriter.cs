using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Xml;
using Casazen.Core.Exceptions;

namespace Casazen.Core.Utilities;

/// <summary>
/// The one writer of the Excel files (<c>.xlsx</c>) the host downloads (FIS-07, NF-87), with nothing but the framework: an xlsx is a zip of XML
/// parts, so it is written with <see cref="ZipArchive"/> and <see cref="XmlWriter"/>, the mirror of the reader of the bank statements
/// (<c>XlsxTableReader</c>), and no package is added. Two ways to write: one sheet, a header row (bold, kept on top when the sheet is scrolled), then the
/// rows (<see cref="Write(string, IReadOnlyList{string}, IEnumerable{IReadOnlyList{object}}, int)"/>, the fiscal documents); or a workbook of the sheets of a
/// report, each its title row, its header and its typed rows (<see cref="Write(IReadOnlyList{ReportSheet}, int)"/>, the host reports, NF-101).
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><b>Cells</b> of the one-sheet file are the ones <see cref="CsvWriter"/> takes: a <see cref="string"/> is a text cell, a <see cref="decimal"/> an
/// amount (a number cell shown to two places, written with the same <c>0.00</c> the CSV uses, so the two files carry the same cent), an integer a number,
/// a <see cref="DateOnly"/> a date cell (a serial number with the date format <c>dd/mm/yyyy</c>), <c>null</c> an empty cell. A number is written
/// as a number and never as text. The cells of a report sheet are <see cref="ReportCell"/>: an amount carries <c>#,##0.00</c>, a percentage is the fraction
/// with <c>0.00%</c>, a day <c>dd/mm/yyyy</c>, and the numbers are written as the exact decimal text of the value, never through a floating point.</item>
/// <item><b>Never a formula.</b> Every text is an inline string cell and this writer has no formula element at all, so a text that starts with
/// <c>= + - @</c> (<see cref="CsvWriter.StartsLikeAFormula"/>, the rule of the CSV) is shown as typed and never calculated; its style carries the
/// quote prefix, so it stays text even when someone edits the cell.</item>
/// <item><b>Safe XML.</b> A character XML 1.0 does not allow (a control code) is left out of a text, and a text is cut at the 32,767
/// characters a cell holds.</item>
/// <item><b>Rows.</b> At most <c>maxRows</c> rows, the header included, as for the CSV: 422 <c>xlsx_too_many_rows</c>.</item>
/// </list>
/// </remarks>
public static class XlsxWriter
{
    public const string ContentType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

    public const int MaxRows = CsvWriter.MaxRows;

    public const int MaxSheetNameLength = 31;

    public const int MaxCellTextLength = 32_767;

    public const int MaxColumns = 16_384;

    private const string Main = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private const string PackageRelationships = "http://schemas.openxmlformats.org/package/2006/relationships";
    private const string DocumentRelationships = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private const string ContentTypesNamespace = "http://schemas.openxmlformats.org/package/2006/content-types";

    private const int StyleDefault = 0;
    private const int StyleHeader = 1;
    private const int StyleAmount = 2;
    private const int StyleDate = 3;
    private const int StyleQuotedText = 4;
    private const int StyleMoney = 5;
    private const int StylePercent = 6;
    private const int StyleHeaderQuoted = 7;

    private const int NumberFormatDate = 164;
    private const int NumberFormatMoney = 165;
    private const int NumberFormatPercent = 166;

    private const int ReportFrozenRows = 2;

    private const int FirstSerialOfTheCalendar = 61;

    private const double MinColumnWidth = 10;
    private const double MaxColumnWidth = 60;
    private const double NumberColumnWidth = 14;

    private static readonly DateOnly SerialEpoch = new(1899, 12, 30);
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    public static byte[] Write(string sheetName, IReadOnlyList<string> header, IEnumerable<IReadOnlyList<object?>> rows, int maxRows = MaxRows)
    {
        ArgumentNullException.ThrowIfNull(header);
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxRows, 1);

        var table = new List<IReadOnlyList<object?>> { header };
        foreach (var row in rows)
        {
            if (table.Count >= maxRows)
                throw new DomainRuleException("xlsx_too_many_rows", "CsvTooManyRows", maxRows);

            ArgumentNullException.ThrowIfNull(row);
            table.Add(row);
        }

        var columns = table.Max(row => row.Count);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(columns, MaxColumns);

        using var output = new MemoryStream();
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            WritePart(zip, "[Content_Types].xml", writer => WriteContentTypes(writer, 1));
            WritePart(zip, "_rels/.rels", WriteRootRelationships);
            WritePart(zip, "xl/workbook.xml", writer => WriteWorkbook(writer, [SheetNameOf(sheetName)]));
            WritePart(zip, "xl/_rels/workbook.xml.rels", writer => WriteWorkbookRelationships(writer, 1));
            WritePart(zip, "xl/styles.xml", WriteStyles);
            WritePart(zip, "xl/worksheets/sheet1.xml", writer => WriteSheet(writer, table, columns));
        }

        return output.ToArray();
    }

    /// <summary>
    /// The workbook for the sheets of a report (NF-101): one sheet for each table, with its title row, its header and its typed rows (<see cref="ReportCell"/>),
    /// the two first rows kept on top when the sheet is scrolled. A number is a number cell (an amount with <c>#,##0.00</c>, a percentage as the fraction with
    /// <c>0.00%</c>), a day a date cell (<c>dd/mm/yyyy</c>), a text an inline string that is never a formula, whatever it starts with; the width of each column
    /// fits its header and its longest text. Each part is streamed into the zip, and no sheet may have more than <paramref name="maxRows"/> rows.
    /// </summary>
    /// <exception cref="DomainRuleException">A sheet has more than <paramref name="maxRows"/> rows, its title and header included (422 <c>xlsx_too_many_rows</c>).</exception>
    public static byte[] Write(IReadOnlyList<ReportSheet> sheets, int maxRows = MaxRows)
    {
        ArgumentNullException.ThrowIfNull(sheets);
        ArgumentOutOfRangeException.ThrowIfLessThan(sheets.Count, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxRows, 1);

        foreach (var sheet in sheets)
        {
            ArgumentNullException.ThrowIfNull(sheet);
            if (sheet.Table.RowCount > maxRows)
                throw new DomainRuleException("xlsx_too_many_rows", "CsvTooManyRows", maxRows);

            ArgumentOutOfRangeException.ThrowIfGreaterThan(ColumnsOf(sheet.Table), MaxColumns);
        }

        var names = SheetNamesOf(sheets);
        using var output = new MemoryStream();
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            WritePart(zip, "[Content_Types].xml", writer => WriteContentTypes(writer, sheets.Count));
            WritePart(zip, "_rels/.rels", WriteRootRelationships);
            WritePart(zip, "xl/workbook.xml", writer => WriteWorkbook(writer, names));
            WritePart(zip, "xl/_rels/workbook.xml.rels", writer => WriteWorkbookRelationships(writer, sheets.Count));
            WritePart(zip, "xl/styles.xml", WriteStyles);
            for (var i = 0; i < sheets.Count; i++)
            {
                var table = sheets[i].Table;
                WritePart(zip, SheetPath(i + 1), writer => WriteReportSheet(writer, table));
            }
        }

        return output.ToArray();
    }

    internal static IReadOnlyList<string> SheetNamesOf(IReadOnlyList<ReportSheet> sheets)
    {
        ArgumentNullException.ThrowIfNull(sheets);

        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var names = new List<string>(sheets.Count);
        foreach (var sheet in sheets)
        {
            var name = SheetNameOf(sheet.Name);
            var unique = name;
            for (var n = 2; !used.Add(unique); n++)
            {
                var suffix = " (" + Number(n) + ")";
                var room = MaxSheetNameLength - suffix.Length;
                unique = (name.Length > room ? name[..room].TrimEnd() : name) + suffix;
            }

            names.Add(unique);
        }

        return names;
    }

    internal static string SheetNameOf(string? name)
    {
        var kept = Clean(name ?? string.Empty);
        var allowed = new string(kept.Where(c => c is not ('[' or ']' or ':' or '*' or '?' or '/' or '\\') && !char.IsSurrogate(c)).ToArray());
        var trimmed = allowed.Trim().Trim('\'').Trim();
        if (trimmed.Length == 0)
            return "Sheet1";

        return trimmed.Length > MaxSheetNameLength ? trimmed[..MaxSheetNameLength].TrimEnd() : trimmed;
    }

    internal static string Clean(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var kept = new StringBuilder(Math.Min(text.Length, MaxCellTextLength));
        for (var i = 0; i < text.Length && kept.Length < MaxCellTextLength - 1; i++)
        {
            var ch = text[i];
            if (char.IsHighSurrogate(ch) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
            {
                kept.Append(ch).Append(text[i + 1]);
                i++;
            }
            else if (XmlConvert.IsXmlChar(ch))
            {
                kept.Append(ch);
            }
        }

        return kept.ToString();
    }

    internal static int? SerialOf(DateOnly day)
    {
        var serial = day.DayNumber - SerialEpoch.DayNumber;
        return serial >= FirstSerialOfTheCalendar ? serial : null;
    }

    internal static string ColumnName(int index)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);

        var name = new StringBuilder();
        for (var n = index + 1; n > 0; n = (n - 1) / 26)
            name.Insert(0, (char)('A' + ((n - 1) % 26)));
        return name.ToString();
    }

    private static void WritePart(ZipArchive zip, string path, Action<XmlWriter> write)
    {
        var entry = zip.CreateEntry(path, CompressionLevel.Optimal);
        using var stream = entry.Open();
        using var writer = XmlWriter.Create(stream, new XmlWriterSettings { Encoding = Utf8, Indent = false, CloseOutput = false });
        writer.WriteStartDocument(standalone: true);
        write(writer);
        writer.WriteEndDocument();
    }

    private static string SheetPath(int number) => "xl/worksheets/sheet" + Number(number) + ".xml";

    private static void WriteContentTypes(XmlWriter writer, int sheetCount)
    {
        writer.WriteStartElement("Types", ContentTypesNamespace);
        TypeByExtension(writer, "rels", "application/vnd.openxmlformats-package.relationships+xml");
        TypeByExtension(writer, "xml", "application/xml");
        TypeByPart(writer, "/xl/workbook.xml", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml");
        for (var sheet = 1; sheet <= sheetCount; sheet++)
            TypeByPart(writer, "/" + SheetPath(sheet), "application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml");
        TypeByPart(writer, "/xl/styles.xml", "application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml");
        writer.WriteEndElement();
    }

    private static void TypeByExtension(XmlWriter writer, string extension, string contentType)
    {
        writer.WriteStartElement("Default", ContentTypesNamespace);
        writer.WriteAttributeString("Extension", extension);
        writer.WriteAttributeString("ContentType", contentType);
        writer.WriteEndElement();
    }

    private static void TypeByPart(XmlWriter writer, string partName, string contentType)
    {
        writer.WriteStartElement("Override", ContentTypesNamespace);
        writer.WriteAttributeString("PartName", partName);
        writer.WriteAttributeString("ContentType", contentType);
        writer.WriteEndElement();
    }

    private static void WriteRootRelationships(XmlWriter writer)
    {
        writer.WriteStartElement("Relationships", PackageRelationships);
        Relationship(writer, "rId1", DocumentRelationships + "/officeDocument", "xl/workbook.xml");
        writer.WriteEndElement();
    }

    private static void WriteWorkbookRelationships(XmlWriter writer, int sheetCount)
    {
        writer.WriteStartElement("Relationships", PackageRelationships);
        for (var sheet = 1; sheet <= sheetCount; sheet++)
            Relationship(writer, "rId" + Number(sheet), DocumentRelationships + "/worksheet", "worksheets/sheet" + Number(sheet) + ".xml");
        Relationship(writer, "rId" + Number(sheetCount + 1), DocumentRelationships + "/styles", "styles.xml");
        writer.WriteEndElement();
    }

    private static void Relationship(XmlWriter writer, string id, string type, string target)
    {
        writer.WriteStartElement("Relationship", PackageRelationships);
        writer.WriteAttributeString("Id", id);
        writer.WriteAttributeString("Type", type);
        writer.WriteAttributeString("Target", target);
        writer.WriteEndElement();
    }

    private static void WriteWorkbook(XmlWriter writer, IReadOnlyList<string> sheetNames)
    {
        writer.WriteStartElement("workbook", Main);
        writer.WriteAttributeString("xmlns", "r", null, DocumentRelationships);
        writer.WriteStartElement("bookViews", Main);
        writer.WriteStartElement("workbookView", Main);
        writer.WriteEndElement();
        writer.WriteEndElement();
        writer.WriteStartElement("sheets", Main);
        for (var i = 0; i < sheetNames.Count; i++)
        {
            writer.WriteStartElement("sheet", Main);
            writer.WriteAttributeString("name", sheetNames[i]);
            writer.WriteAttributeString("sheetId", Number(i + 1));
            writer.WriteAttributeString("id", DocumentRelationships, "rId" + Number(i + 1));
            writer.WriteEndElement();
        }

        writer.WriteEndElement();
        writer.WriteEndElement();
    }

    private static void WriteStyles(XmlWriter writer)
    {
        writer.WriteStartElement("styleSheet", Main);

        writer.WriteStartElement("numFmts", Main);
        writer.WriteAttributeString("count", "3");
        NumberFormat(writer, NumberFormatDate, "dd/mm/yyyy");
        NumberFormat(writer, NumberFormatMoney, "#,##0.00");
        NumberFormat(writer, NumberFormatPercent, "0.00%");
        writer.WriteEndElement();

        writer.WriteStartElement("fonts", Main);
        writer.WriteAttributeString("count", "2");
        Font(writer, bold: false);
        Font(writer, bold: true);
        writer.WriteEndElement();

        writer.WriteStartElement("fills", Main);
        writer.WriteAttributeString("count", "2");
        Fill(writer, "none");
        Fill(writer, "gray125");
        writer.WriteEndElement();

        writer.WriteStartElement("borders", Main);
        writer.WriteAttributeString("count", "1");
        writer.WriteStartElement("border", Main);
        foreach (var side in new[] { "left", "right", "top", "bottom", "diagonal" })
        {
            writer.WriteStartElement(side, Main);
            writer.WriteEndElement();
        }

        writer.WriteEndElement();
        writer.WriteEndElement();

        writer.WriteStartElement("cellStyleXfs", Main);
        writer.WriteAttributeString("count", "1");
        Format(writer, numberFormat: 0, font: 0, withStyleLink: false, applyNumberFormat: false, applyFont: false, quotePrefix: false);
        writer.WriteEndElement();

        writer.WriteStartElement("cellXfs", Main);
        writer.WriteAttributeString("count", "8");
        Format(writer, numberFormat: 0, font: 0, withStyleLink: true, applyNumberFormat: false, applyFont: false, quotePrefix: false);
        Format(writer, numberFormat: 0, font: 1, withStyleLink: true, applyNumberFormat: false, applyFont: true, quotePrefix: false);
        Format(writer, numberFormat: 2, font: 0, withStyleLink: true, applyNumberFormat: true, applyFont: false, quotePrefix: false);
        Format(writer, numberFormat: NumberFormatDate, font: 0, withStyleLink: true, applyNumberFormat: true, applyFont: false, quotePrefix: false);
        Format(writer, numberFormat: 0, font: 0, withStyleLink: true, applyNumberFormat: false, applyFont: false, quotePrefix: true);
        Format(writer, numberFormat: NumberFormatMoney, font: 0, withStyleLink: true, applyNumberFormat: true, applyFont: false, quotePrefix: false);
        Format(writer, numberFormat: NumberFormatPercent, font: 0, withStyleLink: true, applyNumberFormat: true, applyFont: false, quotePrefix: false);
        Format(writer, numberFormat: 0, font: 1, withStyleLink: true, applyNumberFormat: false, applyFont: true, quotePrefix: true);
        writer.WriteEndElement();

        writer.WriteStartElement("cellStyles", Main);
        writer.WriteAttributeString("count", "1");
        writer.WriteStartElement("cellStyle", Main);
        writer.WriteAttributeString("name", "Normal");
        writer.WriteAttributeString("xfId", "0");
        writer.WriteAttributeString("builtinId", "0");
        writer.WriteEndElement();
        writer.WriteEndElement();

        writer.WriteEndElement();
    }

    private static void NumberFormat(XmlWriter writer, int id, string code)
    {
        writer.WriteStartElement("numFmt", Main);
        writer.WriteAttributeString("numFmtId", Number(id));
        writer.WriteAttributeString("formatCode", code);
        writer.WriteEndElement();
    }

    private static void Font(XmlWriter writer, bool bold)
    {
        writer.WriteStartElement("font", Main);
        if (bold)
        {
            writer.WriteStartElement("b", Main);
            writer.WriteEndElement();
        }

        writer.WriteStartElement("sz", Main);
        writer.WriteAttributeString("val", "11");
        writer.WriteEndElement();
        writer.WriteStartElement("name", Main);
        writer.WriteAttributeString("val", "Calibri");
        writer.WriteEndElement();
        writer.WriteEndElement();
    }

    private static void Fill(XmlWriter writer, string pattern)
    {
        writer.WriteStartElement("fill", Main);
        writer.WriteStartElement("patternFill", Main);
        writer.WriteAttributeString("patternType", pattern);
        writer.WriteEndElement();
        writer.WriteEndElement();
    }

    private static void Format(XmlWriter writer, int numberFormat, int font, bool withStyleLink, bool applyNumberFormat, bool applyFont, bool quotePrefix)
    {
        writer.WriteStartElement("xf", Main);
        writer.WriteAttributeString("numFmtId", Number(numberFormat));
        writer.WriteAttributeString("fontId", Number(font));
        writer.WriteAttributeString("fillId", "0");
        writer.WriteAttributeString("borderId", "0");
        if (withStyleLink)
            writer.WriteAttributeString("xfId", "0");
        if (applyNumberFormat)
            writer.WriteAttributeString("applyNumberFormat", "1");
        if (applyFont)
            writer.WriteAttributeString("applyFont", "1");
        if (quotePrefix)
            writer.WriteAttributeString("quotePrefix", "1");
        writer.WriteEndElement();
    }

    private static void WriteSheet(XmlWriter writer, IReadOnlyList<IReadOnlyList<object?>> table, int columns)
    {
        writer.WriteStartElement("worksheet", Main);
        WriteSheetViews(writer, frozenRows: 1);
        WriteSheetFormat(writer);
        if (columns > 0)
            WriteColumns(writer, WidthsOf(table, columns));

        writer.WriteStartElement("sheetData", Main);
        for (var r = 0; r < table.Count; r++)
        {
            writer.WriteStartElement("row", Main);
            writer.WriteAttributeString("r", Number(r + 1));
            var cells = table[r];
            for (var c = 0; c < cells.Count; c++)
                WriteCell(writer, ColumnName(c) + Number(r + 1), cells[c], isHeader: r == 0);
            writer.WriteEndElement();
        }

        writer.WriteEndElement();
        writer.WriteEndElement();
    }

    private static void WriteReportSheet(XmlWriter writer, ReportTable table)
    {
        var columns = ColumnsOf(table);
        writer.WriteStartElement("worksheet", Main);
        WriteSheetViews(writer, ReportFrozenRows);
        WriteSheetFormat(writer);
        if (columns > 0)
            WriteColumns(writer, WidthsOf(table, columns));

        writer.WriteStartElement("sheetData", Main);

        writer.WriteStartElement("row", Main);
        writer.WriteAttributeString("r", "1");
        WriteText(writer, "A1", table.Title, isHeader: true);
        writer.WriteEndElement();

        writer.WriteStartElement("row", Main);
        writer.WriteAttributeString("r", "2");
        for (var c = 0; c < table.Header.Count; c++)
            WriteText(writer, ColumnName(c) + "2", table.Header[c], isHeader: true);
        writer.WriteEndElement();

        for (var r = 0; r < table.Rows.Count; r++)
        {
            var number = Number(r + ReportFrozenRows + 1);
            writer.WriteStartElement("row", Main);
            writer.WriteAttributeString("r", number);
            var cells = table.Rows[r];
            for (var c = 0; c < cells.Count; c++)
                WriteReportCell(writer, ColumnName(c) + number, cells[c]);
            writer.WriteEndElement();
        }

        writer.WriteEndElement();
        writer.WriteEndElement();
    }

    private static void WriteSheetViews(XmlWriter writer, int frozenRows)
    {
        var firstScrolling = "A" + Number(frozenRows + 1);
        writer.WriteStartElement("sheetViews", Main);
        writer.WriteStartElement("sheetView", Main);
        writer.WriteAttributeString("workbookViewId", "0");
        writer.WriteStartElement("pane", Main);
        writer.WriteAttributeString("ySplit", Number(frozenRows));
        writer.WriteAttributeString("topLeftCell", firstScrolling);
        writer.WriteAttributeString("activePane", "bottomLeft");
        writer.WriteAttributeString("state", "frozen");
        writer.WriteEndElement();
        writer.WriteStartElement("selection", Main);
        writer.WriteAttributeString("pane", "bottomLeft");
        writer.WriteAttributeString("activeCell", firstScrolling);
        writer.WriteAttributeString("sqref", firstScrolling);
        writer.WriteEndElement();
        writer.WriteEndElement();
        writer.WriteEndElement();
    }

    private static void WriteSheetFormat(XmlWriter writer)
    {
        writer.WriteStartElement("sheetFormatPr", Main);
        writer.WriteAttributeString("defaultRowHeight", "15");
        writer.WriteEndElement();
    }

    private static void WriteColumns(XmlWriter writer, double[] widths)
    {
        writer.WriteStartElement("cols", Main);
        for (var column = 0; column < widths.Length; column++)
        {
            writer.WriteStartElement("col", Main);
            writer.WriteAttributeString("min", Number(column + 1));
            writer.WriteAttributeString("max", Number(column + 1));
            writer.WriteAttributeString("width", widths[column].ToString("0.##", CultureInfo.InvariantCulture));
            writer.WriteAttributeString("customWidth", "1");
            writer.WriteEndElement();
        }

        writer.WriteEndElement();
    }

    private static int ColumnsOf(ReportTable table)
    {
        var columns = table.Header.Count;
        foreach (var row in table.Rows)
            columns = Math.Max(columns, row.Count);
        return columns;
    }

    private static double[] WidthsOf(ReportTable table, int columns)
    {
        var widths = new double[columns];
        for (var c = 0; c < table.Header.Count; c++)
            widths[c] = LongestLine(table.Header[c]);

        foreach (var row in table.Rows)
        {
            for (var c = 0; c < row.Count; c++)
            {
                var wanted = row[c].Kind switch
                {
                    ReportCellKind.Empty => 0,
                    ReportCellKind.Text => LongestLine(row[c].TextValue ?? string.Empty),
                    _ => NumberColumnWidth - 2,
                };
                widths[c] = Math.Max(widths[c], wanted);
            }
        }

        for (var c = 0; c < columns; c++)
            widths[c] = Math.Clamp(widths[c] + 2, MinColumnWidth, MaxColumnWidth);
        return widths;
    }

    private static void WriteReportCell(XmlWriter writer, string reference, ReportCell cell)
    {
        switch (cell.Kind)
        {
            case ReportCellKind.Text:
                WriteText(writer, reference, cell.TextValue ?? string.Empty, isHeader: false);
                return;
            case ReportCellKind.Integer:
                WriteNumber(writer, reference, cell.WholeValue.ToString(CultureInfo.InvariantCulture), StyleDefault);
                return;
            case ReportCellKind.Decimal:
                WriteNumber(writer, reference, cell.DecimalValue.ToString("0.00", CultureInfo.InvariantCulture), StyleMoney);
                return;
            case ReportCellKind.Percent:
                WriteNumber(writer, reference, cell.DecimalValue.ToString("0.####", CultureInfo.InvariantCulture), StylePercent);
                return;
            case ReportCellKind.Date:
                WriteDay(writer, reference, cell.DayValue, isHeader: false);
                return;
            default:
                return;
        }
    }

    private static void WriteDay(XmlWriter writer, string reference, DateOnly day, bool isHeader)
    {
        if (SerialOf(day) is { } serial)
        {
            WriteNumber(writer, reference, Number(serial), StyleDate);
            return;
        }

        WriteText(writer, reference, day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), isHeader);
    }

    private static double[] WidthsOf(IReadOnlyList<IReadOnlyList<object?>> table, int columns)
    {
        var widths = new double[columns];
        foreach (var row in table)
        {
            for (var c = 0; c < row.Count; c++)
            {
                var wanted = row[c] switch
                {
                    null => 0,
                    string text => LongestLine(text),
                    decimal or DateOnly or int or long or short or byte => NumberColumnWidth - 2,
                    var other => LongestLine(Convert.ToString(other, CultureInfo.InvariantCulture) ?? string.Empty),
                };
                widths[c] = Math.Max(widths[c], wanted);
            }
        }

        for (var c = 0; c < columns; c++)
            widths[c] = Math.Clamp(widths[c] + 2, MinColumnWidth, MaxColumnWidth);
        return widths;
    }

    private static int LongestLine(string text) => text.Split('\n').Max(line => line.Length);

    private static void WriteCell(XmlWriter writer, string reference, object? value, bool isHeader)
    {
        switch (value)
        {
            case null:
                return;
            case string text:
                WriteText(writer, reference, text, isHeader);
                return;
            case decimal amount:
                WriteNumber(writer, reference, amount.ToString("0.00", CultureInfo.InvariantCulture), StyleAmount);
                return;
            case int or long or short or byte:
                WriteNumber(writer, reference, Convert.ToString(value, CultureInfo.InvariantCulture)!, StyleDefault);
                return;
            case DateOnly day:
                WriteDay(writer, reference, day, isHeader);
                return;
            default:
                WriteText(writer, reference, Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty, isHeader);
                return;
        }
    }

    private static void WriteNumber(XmlWriter writer, string reference, string value, int style)
    {
        writer.WriteStartElement("c", Main);
        writer.WriteAttributeString("r", reference);
        if (style != StyleDefault)
            writer.WriteAttributeString("s", Number(style));
        writer.WriteStartElement("v", Main);
        writer.WriteString(value);
        writer.WriteEndElement();
        writer.WriteEndElement();
    }

    private static void WriteText(XmlWriter writer, string reference, string text, bool isHeader)
    {
        var clean = Clean(text);
        if (clean.Length == 0)
            return;

        var quoted = CsvWriter.StartsLikeAFormula(clean);
        var style = isHeader ? (quoted ? StyleHeaderQuoted : StyleHeader) : (quoted ? StyleQuotedText : StyleDefault);
        writer.WriteStartElement("c", Main);
        writer.WriteAttributeString("r", reference);
        if (style != StyleDefault)
            writer.WriteAttributeString("s", Number(style));
        writer.WriteAttributeString("t", "inlineStr");
        writer.WriteStartElement("is", Main);
        writer.WriteStartElement("t", Main);
        if (NeedsToKeepSpaces(clean))
            writer.WriteAttributeString("xml", "space", null, "preserve");
        writer.WriteString(clean);
        writer.WriteEndElement();
        writer.WriteEndElement();
        writer.WriteEndElement();
    }

    private static bool NeedsToKeepSpaces(string text) =>
        char.IsWhiteSpace(text[0]) || char.IsWhiteSpace(text[^1]) || text.Contains("  ", StringComparison.Ordinal) || text.IndexOfAny(['\n', '\t', '\r']) >= 0;

    private static string Number(int value) => value.ToString(CultureInfo.InvariantCulture);
}
