using System.Text;

namespace Casazen.Core.Utilities;

/// <summary>
/// CSV reader that keeps quoted cells intact, including newlines inside quotes (the official ISTAT
/// "Elenco dei comuni italiani" CSV puts line breaks in two header cells).
/// </summary>
public static class QuotedCsv
{
    public static readonly char[] DefaultDelimiters = [';', ',', '\t', '|'];

    /// <summary>One record of a CSV file; <see cref="StartLine"/> is 1-based in the original text.</summary>
    public sealed record Record(int StartLine, IReadOnlyList<string> Cells);

    /// <summary>
    /// Splits <paramref name="text"/> into records. The delimiter is the one that yields the most cells in the
    /// first record; a tie prefers <c>;</c> (ISTAT), then <c>,</c>, tab, <c>|</c>. Empty records are skipped.
    /// </summary>
    public static IReadOnlyList<Record> ReadRecords(string text, IReadOnlyList<char>? delimiters = null)
    {
        ArgumentNullException.ThrowIfNull(text);
        var choices = delimiters ?? DefaultDelimiters;
        var delimiter = DetectDelimiter(text, choices);
        return ReadRecords(text, delimiter);
    }

    public static IReadOnlyList<Record> ReadRecords(string text, char delimiter)
    {
        ArgumentNullException.ThrowIfNull(text);
        var records = new List<Record>();
        var cells = new List<string>();
        var current = new StringBuilder();
        var quoted = false;
        var line = 1;
        var recordStart = 1;

        void EndCell()
        {
            cells.Add(current.ToString());
            current.Clear();
        }

        void EndRecord()
        {
            EndCell();
            if (cells.Any(c => !string.IsNullOrWhiteSpace(c)))
                records.Add(new Record(recordStart, cells.ToArray()));
            cells = [];
            quoted = false;
        }

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < text.Length && text[i + 1] == '"')
                {
                    current.Append('"');
                    i++;
                }
                else if (c == '"')
                {
                    quoted = false;
                }
                else
                {
                    if (c == '\n')
                        line++;
                    current.Append(c);
                }

                continue;
            }

            if (c == '"' && current.Length == 0)
            {
                quoted = true;
                continue;
            }

            if (c == delimiter)
            {
                EndCell();
                continue;
            }

            if (c == '\r')
            {
                if (i + 1 < text.Length && text[i + 1] == '\n')
                    i++;
                line++;
                EndRecord();
                recordStart = line;
                continue;
            }

            if (c == '\n')
            {
                line++;
                EndRecord();
                recordStart = line;
                continue;
            }

            current.Append(c);
        }

        if (quoted || current.Length > 0 || cells.Count > 0)
            EndRecord();

        return records;
    }

    public static char DetectDelimiter(string text, IReadOnlyList<char> delimiters)
    {
        var counts = delimiters.ToDictionary(d => d, _ => 0);
        var quoted = false;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < text.Length && text[i + 1] == '"')
                    i++;
                else if (c == '"')
                    quoted = false;
                continue;
            }

            if (c == '"')
            {
                quoted = true;
                continue;
            }

            if (c is '\r' or '\n')
                break;

            if (counts.ContainsKey(c))
                counts[c]++;
        }

        var best = 0;
        var chosen = delimiters[0];
        foreach (var delimiter in delimiters)
        {
            if (counts[delimiter] > best)
            {
                best = counts[delimiter];
                chosen = delimiter;
            }
        }

        return chosen;
    }
}
