using System.Globalization;
using System.Text;
using Casazen.Core.Exceptions;
using Casazen.Core.Services;

namespace Casazen.Infrastructure.Services;

internal sealed record ImportColumns(int? PropertyId, int? CinCode, int? Comune, int? Region, int? Rooms, int? LockCode);

internal sealed class ParsedImportRow(int number)
{
    public int Number { get; } = number;

    public string? PropertyId { get; init; }

    public string? Cin { get; init; }

    public string? Comune { get; init; }

    public string? Region { get; init; }

    public string? Rooms { get; init; }

    public string? LockCode { get; init; }
}

internal sealed record ParsedImport(ImportColumns Columns, IReadOnlyList<ParsedImportRow> Rows, IReadOnlyList<string> UnknownColumns);

/// <summary>
/// Reads the CSV of the bulk import (E10/F02/US01/T02) into rows of text and understands the header of a CSV or of an Excel sheet. No reader of
/// either exists elsewhere for this and no package is added: the CSV is parsed here, the Excel by <see cref="OnboardingXlsxReader"/>.
/// <list type="bullet">
/// <item>UTF-8 (a byte-order mark is dropped); a file that is not valid UTF-8 is read as Windows-1252/Latin-1 text, which is what the CSV of an
/// Italian spreadsheet is, so a code is never mangled (every column of the import is ASCII).</item>
/// <item>The delimiter is the one of <c>,</c> and <c>;</c> that the header line has more of, outside quotes; <c>,</c> when it has neither.</item>
/// <item>Fields may be quoted (<c>"a;b"</c>, <c>""</c> for a quote) and span lines; <c>\r\n</c>, <c>\n</c> and <c>\r</c> end a row. A quote that is
/// never closed is a file that cannot be read.</item>
/// <item>A blank row is skipped and still counts in the row numbers, which are the ones a person sees in the sheet (the header is row 1).</item>
/// </list>
/// </summary>
internal static class OnboardingBulkImportParser
{
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private static readonly Dictionary<string, string> Aliases = BuildAliases();

    public static bool LooksBinary(byte[] content)
    {
        if (OnboardingXlsxReader.LooksLikeZip(content))
            return true;

        return content.AsSpan(0, Math.Min(content.Length, 4096)).Contains((byte)0);
    }

    public static List<ImportTableRow> ReadCsv(byte[] content)
    {
        ArgumentNullException.ThrowIfNull(content);
        var text = Decode(content);
        var delimiter = DetectDelimiter(text);

        var rows = new List<ImportTableRow>();
        var cells = new List<string>();
        var field = new StringBuilder();
        var number = 1;
        var inQuotes = false;
        var fieldStarted = false;

        void EndField()
        {
            if (cells.Count < BulkImportLimits.MaxColumns)
                cells.Add(field.ToString());

            field.Clear();
            fieldStarted = false;
        }

        void EndRow()
        {
            EndField();
            if (cells.Any(cell => !string.IsNullOrWhiteSpace(cell)))
            {
                rows.Add(new ImportTableRow(number, [.. cells]));
                if (rows.Count > BulkImportLimits.MaxRows + 1)
                    throw new DomainRuleException(BulkImportCodes.TooManyRows, "OnboardingImportTooManyRows", BulkImportLimits.MaxRows);
            }

            cells.Clear();
            number++;
        }

        void Append(char ch)
        {
            fieldStarted = true;
            if (field.Length < BulkImportLimits.MaxCellLength)
                field.Append(ch);
        }

        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            if (inQuotes)
            {
                if (ch != '"')
                {
                    Append(ch);
                }
                else if (i + 1 < text.Length && text[i + 1] == '"')
                {
                    Append('"');
                    i++;
                }
                else
                {
                    inQuotes = false;
                }

                continue;
            }

            if (ch == '"' && !fieldStarted)
            {
                inQuotes = true;
                fieldStarted = true;
            }
            else if (ch == delimiter)
            {
                EndField();
            }
            else if (ch == '\r' || ch == '\n')
            {
                if (ch == '\r' && i + 1 < text.Length && text[i + 1] == '\n')
                    i++;

                EndRow();
            }
            else
            {
                Append(ch);
            }
        }

        if (inQuotes)
            throw new DomainRuleException(BulkImportCodes.FileUnreadable, "OnboardingImportFileUnreadable");

        if (fieldStarted || field.Length > 0 || cells.Count > 0)
            EndRow();

        return rows;
    }

    private static string Decode(byte[] content)
    {
        var bytes = content.AsSpan();
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            bytes = bytes[3..];

        try
        {
            return StrictUtf8.GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            return Encoding.Latin1.GetString(bytes);
        }
    }

    private static char DetectDelimiter(string text)
    {
        var inQuotes = false;
        var semicolons = 0;
        var commas = 0;
        var seenContent = false;
        foreach (var ch in text)
        {
            if (ch == '"')
            {
                inQuotes = !inQuotes;
                seenContent = true;
            }
            else if (!inQuotes && (ch == '\r' || ch == '\n'))
            {
                if (seenContent)
                    break;
            }
            else if (!inQuotes && ch == ';')
            {
                semicolons++;
                seenContent = true;
            }
            else if (!inQuotes && ch == ',')
            {
                commas++;
                seenContent = true;
            }
            else if (!char.IsWhiteSpace(ch))
            {
                seenContent = true;
            }
        }

        return semicolons > commas ? ';' : ',';
    }

    public static ParsedImport Parse(IReadOnlyList<ImportTableRow> table)
    {
        ArgumentNullException.ThrowIfNull(table);
        if (table.Count == 0)
            throw new DomainRuleException(BulkImportCodes.FileEmpty, "OnboardingImportFileEmpty");

        var header = table[0].Cells;
        var found = new Dictionary<string, int>(StringComparer.Ordinal);
        var unknown = new List<string>();
        for (var column = 0; column < header.Count; column++)
        {
            var title = header[column].Trim();
            if (title.Length == 0)
                continue;

            if (Aliases.TryGetValue(Key(title), out var name) && found.TryAdd(name, column))
                continue;

            var shown = title.Length <= 50 ? title : title[..50];
            if (unknown.Count < 20 && !unknown.Contains(shown, StringComparer.Ordinal))
                unknown.Add(shown);
        }

        int? Position(string name) => found.TryGetValue(name, out var column) ? column : null;
        var columns = new ImportColumns(
            Position(BulkImportColumns.PropertyId),
            Position(BulkImportColumns.CinCode),
            Position(BulkImportColumns.ComuneIstatCode),
            Position(BulkImportColumns.RegionCode),
            Position(BulkImportColumns.AlloggiatiRoomNumbers),
            Position(BulkImportColumns.LockCode));

        if ((columns.PropertyId is null && columns.CinCode is null)
            || (columns.Comune is null && columns.Region is null && columns.Rooms is null && columns.LockCode is null))
        {
            throw new DomainRuleException(BulkImportCodes.ColumnsUnrecognized, "OnboardingImportColumnsUnrecognized");
        }

        var data = table.Skip(1).ToList();
        if (data.Count == 0)
            throw new DomainRuleException(BulkImportCodes.FileEmpty, "OnboardingImportFileEmpty");
        if (data.Count > BulkImportLimits.MaxRows)
            throw new DomainRuleException(BulkImportCodes.TooManyRows, "OnboardingImportTooManyRows", BulkImportLimits.MaxRows);

        var rows = data
            .Select(row => new ParsedImportRow(row.Number)
            {
                PropertyId = Cell(row, columns.PropertyId),
                Cin = Cell(row, columns.CinCode),
                Comune = Cell(row, columns.Comune),
                Region = Cell(row, columns.Region),
                Rooms = Cell(row, columns.Rooms),
                LockCode = Cell(row, columns.LockCode),
            })
            .ToList();
        return new ParsedImport(columns, rows, unknown);
    }

    private static string? Cell(ImportTableRow row, int? column)
    {
        if (column is not { } index || index >= row.Cells.Count)
            return null;

        var text = row.Cells[index].Trim();
        return text.Length == 0 ? null : text;
    }

    internal static string Key(string title)
    {
        var decomposed = title.Normalize(NormalizationForm.FormD);
        var key = new StringBuilder(decomposed.Length);
        foreach (var ch in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark)
                continue;

            if (char.IsLetterOrDigit(ch))
                key.Append(char.ToLowerInvariant(ch));
        }

        return key.ToString();
    }

    private static Dictionary<string, string> BuildAliases()
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);

        void Add(string column, params string[] names)
        {
            foreach (var name in names)
                map[Key(name)] = column;
        }

        Add(BulkImportColumns.PropertyId, "propertyId", "property id", "id immobile", "immobile id", "id struttura");
        Add(BulkImportColumns.CinCode, "cinCode", "cin", "cin code", "codice cin", "cin immobile");
        Add(
            BulkImportColumns.ComuneIstatCode,
            "comuneIstatCode",
            "comune istat code",
            "comune istat",
            "istat code",
            "istat",
            "codice istat",
            "codice istat comune",
            "codice comune istat");
        Add(BulkImportColumns.RegionCode, "regionCode", "region code", "region", "regione", "codice regione");
        Add(
            BulkImportColumns.AlloggiatiRoomNumbers,
            "alloggiatiRoomNumbers",
            "alloggiati room numbers",
            "alloggiati rooms",
            "room numbers",
            "numeri camera alloggiati",
            "numeri camere alloggiati",
            "numero camera alloggiati",
            "numeri stanza alloggiati",
            "camere alloggiati",
            "numeri camere");
        Add(
            BulkImportColumns.LockCode,
            "lockCode",
            "lock code",
            "codice serratura",
            "codice lock",
            "codice cassetta",
            "codice accesso",
            "access code",
            "door code",
            "serratura");
        return map;
    }
}
