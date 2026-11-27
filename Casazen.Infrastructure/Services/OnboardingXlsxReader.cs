using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Xml;
using Casazen.Core.Exceptions;
using Casazen.Core.Services;

namespace Casazen.Infrastructure.Services;

internal sealed record ImportTableRow(int Number, IReadOnlyList<string> Cells);

/// <summary>
/// Reads the first sheet of an Excel workbook (<c>.xlsx</c>) of the bulk import (E10/F02/US01/T02) into rows of text, with nothing but the
/// framework: an <c>.xlsx</c> is a zip of XML parts (<see cref="ZipArchive"/> and <see cref="XmlReader"/>), so no package is added. The file comes
/// from outside, so it is read defensively:
/// <list type="bullet">
/// <item>An archive of more than <see cref="BulkImportLimits.MaxZipEntries"/> entries, or that declares more than
/// <see cref="BulkImportLimits.MaxUnzippedBytes"/> once unzipped, is refused up front (a zip bomb says so); and because a header can lie, every
/// byte actually read from the parts is counted against the same limit.</item>
/// <item>Only the parts the reading needs are opened: the workbook and its relationships (to find the first sheet), the shared strings and that
/// sheet. Nothing else of the archive is read, and nothing is written anywhere.</item>
/// <item>XML is read with DTDs prohibited and no resolver: no external entity can be reached.</item>
/// <item>A formula is never evaluated, nor even read: a formula cell yields its cached value, or nothing when it has none.</item>
/// <item>Rows and columns are bounded (<see cref="BulkImportLimits.MaxRows"/>, <see cref="BulkImportLimits.MaxColumns"/>) and a text is cut at
/// <see cref="BulkImportLimits.MaxCellLength"/>.</item>
/// </list>
/// The reader keeps a cell's text as the file holds it: a number is its stored text (a code written as a number has lost its leading zeros in the
/// file already).
/// </summary>
internal static class OnboardingXlsxReader
{
    private const string WorkbookPath = "xl/workbook.xml";
    private const string RelationshipsPath = "xl/_rels/workbook.xml.rels";
    private const string SharedStringsPath = "xl/sharedStrings.xml";
    private const string RelationshipNamespace = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private const int MaxSharedStrings = 200_000;

    public static bool LooksLikeZip(byte[] content) =>
        content.Length >= 4 && content[0] == (byte)'P' && content[1] == (byte)'K' && content[2] == 3 && content[3] == 4;

    public static List<ImportTableRow> Read(byte[] content)
    {
        ArgumentNullException.ThrowIfNull(content);
        try
        {
            using var zip = new ZipArchive(new MemoryStream(content, writable: false), ZipArchiveMode.Read);
            if (zip.Entries.Count > BulkImportLimits.MaxZipEntries)
                throw Unreadable();

            long declared = 0;
            foreach (var entry in zip.Entries)
            {
                declared += entry.Length;
                if (declared > BulkImportLimits.MaxUnzippedBytes)
                    throw TooLarge();
            }

            var budget = new ByteBudget(BulkImportLimits.MaxUnzippedBytes);
            var sheetPath = FindFirstSheetPath(zip, budget);
            var shared = ReadSharedStrings(zip, budget);
            var sheet = zip.GetEntry(sheetPath) ?? throw Unreadable();
            return ReadSheet(sheet, shared, budget);
        }
        catch (Exception ex) when (ex is ExpandsTooMuchException || ex.InnerException is ExpandsTooMuchException)
        {
            throw TooLarge();
        }
        catch (Exception ex) when (ex is InvalidDataException or XmlException or IOException or NotSupportedException)
        {
            throw Unreadable();
        }
    }

    private static string FindFirstSheetPath(ZipArchive zip, ByteBudget budget)
    {
        var workbook = zip.GetEntry(WorkbookPath) ?? throw Unreadable();
        string? relationshipId = null;
        using (var reader = OpenXml(workbook, budget))
        {
            while (reader.Read())
            {
                if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "sheet")
                {
                    relationshipId = reader.GetAttribute("id", RelationshipNamespace);
                    break;
                }
            }
        }

        if (string.IsNullOrEmpty(relationshipId))
            throw Unreadable();

        var relationships = zip.GetEntry(RelationshipsPath) ?? throw Unreadable();
        using var rels = OpenXml(relationships, budget);
        while (rels.Read())
        {
            if (rels.NodeType == XmlNodeType.Element
                && rels.LocalName == "Relationship"
                && string.Equals(rels.GetAttribute("Id"), relationshipId, StringComparison.Ordinal)
                && rels.GetAttribute("Target") is { Length: > 0 } target)
            {
                return target.StartsWith('/') ? target[1..] : $"xl/{target}";
            }
        }

        throw Unreadable();
    }

    private static List<string> ReadSharedStrings(ZipArchive zip, ByteBudget budget)
    {
        var strings = new List<string>();
        if (zip.GetEntry(SharedStringsPath) is not { } entry)
            return strings;

        using var reader = OpenXml(entry, budget);
        while (!reader.EOF)
        {
            if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "si")
            {
                strings.Add(ReadRichText(reader));
                if (strings.Count > MaxSharedStrings)
                    throw Unreadable();

                continue;
            }

            reader.Read();
        }

        return strings;
    }

    private static List<ImportTableRow> ReadSheet(ZipArchiveEntry sheet, IReadOnlyList<string> shared, ByteBudget budget)
    {
        var rows = new List<ImportTableRow>();
        var previous = 0;
        using var reader = OpenXml(sheet, budget);
        while (!reader.EOF)
        {
            if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "row")
            {
                var number = int.TryParse(reader.GetAttribute("r"), NumberStyles.None, CultureInfo.InvariantCulture, out var stated) && stated > 0
                    ? stated
                    : previous + 1;
                previous = number;
                var cells = ReadRow(reader, shared);
                if (cells.Any(cell => !string.IsNullOrWhiteSpace(cell)))
                {
                    rows.Add(new ImportTableRow(number, cells));
                    if (rows.Count > BulkImportLimits.MaxRows + 1)
                        throw new DomainRuleException(BulkImportCodes.TooManyRows, "OnboardingImportTooManyRows", BulkImportLimits.MaxRows);
                }

                continue;
            }

            reader.Read();
        }

        return rows;
    }

    private static List<string> ReadRow(XmlReader reader, IReadOnlyList<string> shared)
    {
        var cells = new List<string>();
        if (reader.IsEmptyElement)
        {
            reader.Read();
            return cells;
        }

        var depth = reader.Depth;
        reader.Read();
        while (!reader.EOF && !(reader.NodeType == XmlNodeType.EndElement && reader.Depth == depth))
        {
            if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "c")
            {
                var column = ColumnIndex(reader.GetAttribute("r")) ?? cells.Count;
                var text = ReadCell(reader, reader.GetAttribute("t"), shared);
                if (column < BulkImportLimits.MaxColumns)
                {
                    while (cells.Count <= column)
                        cells.Add(string.Empty);

                    cells[column] = text;
                }

                continue;
            }

            reader.Read();
        }

        reader.Read();
        return cells;
    }

    private static string ReadCell(XmlReader reader, string? type, IReadOnlyList<string> shared)
    {
        if (reader.IsEmptyElement)
        {
            reader.Read();
            return string.Empty;
        }

        string? value = null;
        string? inline = null;
        var depth = reader.Depth;
        reader.Read();
        while (!reader.EOF && !(reader.NodeType == XmlNodeType.EndElement && reader.Depth == depth))
        {
            if (reader.NodeType == XmlNodeType.Element)
            {
                switch (reader.LocalName)
                {
                    case "v":
                        value = reader.ReadElementContentAsString();
                        continue;
                    case "is":
                        inline = ReadRichText(reader);
                        continue;
                    default:
                        reader.Skip();
                        continue;
                }
            }

            reader.Read();
        }

        reader.Read();
        return type switch
        {
            "s" => int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var index) && index >= 0 && index < shared.Count
                ? shared[index]
                : string.Empty,
            "inlineStr" => Clip(inline ?? value),
            "str" or "d" => Clip(value),
            "b" or "e" => string.Empty,
            _ => Clip(value?.Trim()),
        };
    }

    private static string ReadRichText(XmlReader reader)
    {
        if (reader.IsEmptyElement)
        {
            reader.Read();
            return string.Empty;
        }

        var text = new StringBuilder();
        var depth = reader.Depth;
        reader.Read();
        while (!reader.EOF && !(reader.NodeType == XmlNodeType.EndElement && reader.Depth == depth))
        {
            if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "rPh")
            {
                reader.Skip();
                continue;
            }

            if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "t")
            {
                var part = reader.ReadElementContentAsString();
                if (text.Length < BulkImportLimits.MaxCellLength)
                    text.Append(part);

                continue;
            }

            reader.Read();
        }

        reader.Read();
        return Clip(text.ToString());
    }

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

    private static string Clip(string? value) =>
        string.IsNullOrEmpty(value) ? string.Empty : value.Length <= BulkImportLimits.MaxCellLength ? value : value[..BulkImportLimits.MaxCellLength];

    private static XmlReader OpenXml(ZipArchiveEntry entry, ByteBudget budget) =>
        XmlReader.Create(
            new BudgetStream(entry.Open(), budget),
            new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                IgnoreComments = true,
                IgnoreProcessingInstructions = true,
                MaxCharactersInDocument = BulkImportLimits.MaxUnzippedBytes,
                CloseInput = true,
            });

    private static DomainRuleException Unreadable() => new(BulkImportCodes.FileUnreadable, "OnboardingImportFileUnreadable");

    private static DomainPayloadTooLargeException TooLarge() =>
        new(BulkImportCodes.FileTooLarge, "OnboardingImportFileTooLarge", BulkImportLimits.MaxFileBytes / (1024 * 1024));

    private sealed class ByteBudget(long limit)
    {
        private long _used;

        public void Spend(int bytes)
        {
            _used += bytes;
            if (_used > limit)
                throw new ExpandsTooMuchException();
        }
    }

    private sealed class ExpandsTooMuchException : Exception;

    private sealed class BudgetStream(Stream inner, ByteBudget budget) : Stream
    {
        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) => Spend(inner.Read(buffer, offset, count));

        public override int Read(Span<byte> buffer) => Spend(inner.Read(buffer));

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

        private int Spend(int read)
        {
            budget.Spend(read);
            return read;
        }
    }
}
