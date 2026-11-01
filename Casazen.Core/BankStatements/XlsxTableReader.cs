using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Xml;
using Casazen.Core.Exceptions;

namespace Casazen.Core.BankStatements;

/// <summary>
/// Reads the first sheet of an Excel workbook (<c>.xlsx</c>) into rows of cells, with nothing but the framework: an xlsx is a zip of XML
/// parts. The file comes from outside, so every part is read through a limit on what it may unzip to (a zip that declares a small size
/// and holds a large one is cut), XML is read without DTDs or external entities, and nothing is written anywhere.
/// </summary>
internal static class XlsxTableReader
{
    private const string WorkbookPath = "xl/workbook.xml";
    private const string RelationshipsPath = "xl/_rels/workbook.xml.rels";
    private const string SharedStringsPath = "xl/sharedStrings.xml";
    private const string DefaultSheetPath = "xl/worksheets/sheet1.xml";
    private const string RelationshipNamespace = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private const int MaxEntries = 2_000;
    private const int MaxSharedStrings = 200_000;
    private const long MaxSharedStringChars = 24L * 1024 * 1024;

    /// <summary>True when the bytes are a zip with an Excel workbook inside.</summary>
    public static bool LooksLikeWorkbook(byte[] content)
    {
        try
        {
            using var zip = new ZipArchive(new MemoryStream(content, writable: false), ZipArchiveMode.Read);
            return zip.GetEntry(WorkbookPath) is not null;
        }
        catch (InvalidDataException)
        {
            return false;
        }
    }

    public static List<Cell[]> Read(byte[] content)
    {
        using var zip = new ZipArchive(new MemoryStream(content, writable: false), ZipArchiveMode.Read);
        if (zip.Entries.Count > MaxEntries)
            throw Unreadable();

        var shared = ReadSharedStrings(zip);
        var sheet = zip.GetEntry(FindFirstSheetPath(zip)) ?? zip.GetEntry(DefaultSheetPath) ?? throw Unreadable();
        return ReadSheet(sheet, shared);
    }

    private static XmlReaderSettings Settings() => new()
    {
        DtdProcessing = DtdProcessing.Prohibit,
        XmlResolver = null,
        IgnoreComments = true,
        IgnoreProcessingInstructions = true,
        MaxCharactersInDocument = BankStatementLimits.MaxUnzippedPartBytes,
        MaxCharactersFromEntities = 0,
    };

    private static Stream Open(ZipArchiveEntry entry)
    {
        if (entry.Length > BankStatementLimits.MaxUnzippedPartBytes)
            throw Unreadable();
        return new LimitedReadStream(entry.Open(), BankStatementLimits.MaxUnzippedPartBytes);
    }

    private static DomainRuleException Unreadable() => new(BankStatementErrorCodes.Unreadable, "BankStatementUnreadable");

    // ---------------------------------------------------------------- workbook

    private static string FindFirstSheetPath(ZipArchive zip)
    {
        string? relationshipId = null;
        if (zip.GetEntry(WorkbookPath) is { } workbook)
        {
            using var stream = Open(workbook);
            using var reader = XmlReader.Create(stream, Settings());
            while (reader.Read())
            {
                if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "sheet")
                {
                    relationshipId = reader.GetAttribute("id", RelationshipNamespace);
                    break;
                }
            }
        }

        if (relationshipId is not null && zip.GetEntry(RelationshipsPath) is { } relationships)
        {
            using var stream = Open(relationships);
            using var reader = XmlReader.Create(stream, Settings());
            while (reader.Read())
            {
                if (reader.NodeType == XmlNodeType.Element
                    && reader.LocalName == "Relationship"
                    && string.Equals(reader.GetAttribute("Id"), relationshipId, StringComparison.Ordinal)
                    && reader.GetAttribute("Target") is { Length: > 0 } target)
                {
                    return target.StartsWith('/') ? target[1..] : $"xl/{target}";
                }
            }
        }

        return DefaultSheetPath;
    }

    private static List<string> ReadSharedStrings(ZipArchive zip)
    {
        var strings = new List<string>();
        if (zip.GetEntry(SharedStringsPath) is not { } entry)
            return strings;

        long characters = 0;
        using var stream = Open(entry);
        using var reader = XmlReader.Create(stream, Settings());
        reader.MoveToContent();
        if (reader.IsEmptyElement)
            return strings;

        reader.Read();
        while (!reader.EOF)
        {
            if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "si")
            {
                var text = ReadSharedString(reader, ref characters);
                if (strings.Count >= MaxSharedStrings || characters > MaxSharedStringChars)
                    throw Unreadable();
                strings.Add(BankValues.Clip(text));
                continue;
            }

            reader.Read();
        }

        return strings;
    }

    /// <summary>
    /// The text of one shared string (<c>&lt;si&gt;</c>): its <c>&lt;t&gt;</c> elements, plain or in the runs of rich text, but not the phonetic
    /// hints (<c>&lt;rPh&gt;</c>). Read node by node, so an item of millions of empty runs costs one node at a time and never a tree in memory.
    /// The reader ends after the item; <paramref name="characters"/> counts everything read, kept or not.
    /// </summary>
    private static string ReadSharedString(XmlReader reader, ref long characters)
    {
        var depth = reader.Depth;
        var isEmpty = reader.IsEmptyElement;
        reader.Read();
        if (isEmpty)
            return string.Empty;

        var text = new StringBuilder();
        var phonetic = -1;
        while (!reader.EOF && !(reader.NodeType == XmlNodeType.EndElement && reader.Depth == depth))
        {
            if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "rPh" && phonetic < 0 && !reader.IsEmptyElement)
            {
                phonetic = reader.Depth;
            }
            else if (reader.NodeType == XmlNodeType.EndElement && reader.Depth == phonetic)
            {
                phonetic = -1;
            }
            else if (phonetic < 0 && reader.NodeType == XmlNodeType.Element && reader.LocalName == "t")
            {
                var part = reader.ReadElementContentAsString();
                characters += part.Length;
                if (characters > MaxSharedStringChars)
                    throw Unreadable();
                if (text.Length < BankStatementLimits.MaxTextLength)
                    text.Append(part);
                continue;
            }

            reader.Read();
        }

        reader.Read();
        return text.ToString();
    }

    // ---------------------------------------------------------------- sheet

    private static List<Cell[]> ReadSheet(ZipArchiveEntry sheet, IReadOnlyList<string> shared)
    {
        var rows = new List<Cell[]>();
        using var stream = Open(sheet);
        using var reader = XmlReader.Create(stream, Settings());
        while (reader.Read())
        {
            if (reader.NodeType != XmlNodeType.Element || reader.LocalName != "row")
                continue;

            rows.Add(ReadRow(reader, shared));
            if (rows.Count > BankStatementLimits.MaxRows)
                throw new DomainRuleException(BankStatementErrorCodes.TooManyRows, "BankStatementTooManyRows", BankStatementLimits.MaxCredits);
        }

        return rows;
    }

    private static Cell[] ReadRow(XmlReader parent, IReadOnlyList<string> shared)
    {
        var cells = new List<Cell>();
        using var row = parent.ReadSubtree();
        row.Read();
        while (row.Read())
        {
            if (row.NodeType != XmlNodeType.Element || row.LocalName != "c")
                continue;

            var reference = row.GetAttribute("r");
            var type = row.GetAttribute("t");
            var cell = ReadCell(row, type, shared);
            var column = ColumnIndex(reference) ?? cells.Count;
            if (column >= BankStatementLimits.MaxColumns)
                continue;

            while (cells.Count < column)
                cells.Add(Cell.Empty);
            if (cells.Count == column)
                cells.Add(cell);
            else
                cells[column] = cell;
        }

        return cells.ToArray();
    }

    private static Cell ReadCell(XmlReader parent, string? type, IReadOnlyList<string> shared)
    {
        string? value = null;
        string? inline = null;
        using (var cell = parent.ReadSubtree())
        {
            cell.Read();
            if (!cell.IsEmptyElement)
            {
                cell.Read();
                while (!cell.EOF)
                {
                    if (cell.NodeType == XmlNodeType.Element && cell.LocalName == "v")
                    {
                        value = cell.ReadElementContentAsString();
                        continue;
                    }

                    if (cell.NodeType == XmlNodeType.Element && cell.LocalName == "is")
                    {
                        // An empty <is/> has no end tag to stop on: the reader would stay on it and the loop would read it again, for ever.
                        if (cell.IsEmptyElement)
                            cell.Read();
                        else
                            inline = ReadInlineString(cell);
                        continue;
                    }

                    cell.Read();
                }
            }
        }

        switch (type)
        {
            case "s":
                return int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var index) && index >= 0 && index < shared.Count
                    ? new Cell(shared[index], false)
                    : Cell.Empty;
            case "inlineStr":
                return new Cell(BankValues.Clip(inline ?? value), false);
            case "str" or "d":
                return new Cell(BankValues.Clip(value), false);
            case "b" or "e":
                return Cell.Empty;
            default:
                return string.IsNullOrWhiteSpace(value) ? Cell.Empty : new Cell(value.Trim(), true);
        }
    }

    private static string ReadInlineString(XmlReader parent)
    {
        var text = new StringBuilder();
        using var inline = parent.ReadSubtree();
        inline.Read();
        while (!inline.EOF)
        {
            if (inline.NodeType == XmlNodeType.Element && inline.LocalName == "t")
            {
                text.Append(inline.ReadElementContentAsString());
                continue;
            }

            inline.Read();
        }

        return text.ToString();
    }

    /// <summary>Zero-based column of a cell reference such as <c>C12</c> (<c>A</c> is 0, <c>AA</c> is 26); null when the reference is missing or odd.</summary>
    internal static int? ColumnIndex(string? reference)
    {
        if (string.IsNullOrEmpty(reference))
            return null;

        var column = 0;
        var letters = 0;
        foreach (var ch in reference)
        {
            if (!char.IsAsciiLetter(ch))
                break;
            column = (column * 26) + (char.ToUpperInvariant(ch) - 'A' + 1);
            letters++;
            if (letters > 3)
                return null;
        }

        return letters == 0 ? null : column - 1;
    }

    /// <summary>A stream that refuses to give more than a set number of bytes: what a zip entry unzips to is checked here, not trusted from its header.</summary>
    private sealed class LimitedReadStream(Stream inner, long limit) : Stream
    {
        private long _read;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) => Count(inner.Read(buffer, offset, count));

        public override int Read(Span<byte> buffer) => Count(inner.Read(buffer));

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                inner.Dispose();
            base.Dispose(disposing);
        }

        private int Count(int read)
        {
            _read += read;
            if (_read > limit)
                throw new InvalidDataException("The part unzips to more than the importer reads.");
            return read;
        }
    }
}
