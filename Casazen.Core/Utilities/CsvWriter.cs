using System.Globalization;
using System.Text;
using Casazen.Core.Exceptions;

namespace Casazen.Core.Utilities;

/// <summary>
/// The one writer of the CSV files the host downloads (SR-10): for a spreadsheet set to Italian, UTF-8 with a BOM, <c>;</c> between
/// the cells, <c>CRLF</c> after each row, a decimal comma and no thousands separator. And safe to open: a text cell that a
/// spreadsheet would run as a formula (it starts with <c>= + - @</c>, a tab or a CR) gets a leading apostrophe, whatever the file is
/// about; a cell with the separator, a quote or a line break is quoted with the quotes doubled.
/// </summary>
/// <remarks>
/// A cell is a <see cref="string"/> (text, always guarded), a <see cref="decimal"/> (an amount, to two places), an integer or
/// <c>null</c> (empty). A number is written by the writer and never as text, so a negative amount is a number and not a formula.
/// The fiscal reports write through it too (<c>FiscalReportDocuments</c>), without a limit of rows: their output is the same as before.
/// </remarks>
public static class CsvWriter
{
    /// <summary>Between the cells of a row.</summary>
    public const char Separator = ';';

    /// <summary>The most rows a file may have, the header included.</summary>
    public const int MaxRows = 10_000;

    /// <summary>For a file that has always been written whole (the fiscal reports).</summary>
    public const int NoLimit = int.MaxValue;

    private static readonly CultureInfo Italian = CultureInfo.GetCultureInfo("it-IT");

    /// <summary>
    /// The file for <paramref name="rows"/>, header and title rows included as they come: the BOM, then each row of cells joined by
    /// <see cref="Separator"/> and ended by <c>CRLF</c>.
    /// </summary>
    /// <exception cref="DomainRuleException">There are more than <paramref name="maxRows"/> rows (422 <c>csv_too_many_rows</c>).</exception>
    public static byte[] Write(IEnumerable<IReadOnlyList<object?>> rows, int maxRows = MaxRows)
    {
        ArgumentNullException.ThrowIfNull(rows);

        var text = new StringBuilder();
        var count = 0;
        foreach (var row in rows)
        {
            if (++count > maxRows)
                throw new DomainRuleException("csv_too_many_rows", "CsvTooManyRows", maxRows);

            text.Append(string.Join(Separator, row.Select(Cell))).Append("\r\n");
        }

        return [.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes(text.ToString())];
    }

    /// <summary>One cell as it is written: text guarded, a number in Italian, <c>null</c> empty.</summary>
    public static string Cell(object? value) => value switch
    {
        null => string.Empty,
        string text => Text(text),
        decimal amount => amount.ToString("0.00", Italian),
        int or long or short or byte => Convert.ToString(value, CultureInfo.InvariantCulture)!,
        _ => Text(Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty),
    };

    /// <summary>
    /// A text cell: quoted when it holds the separator, a quote or a line break, and with a leading apostrophe when a spreadsheet
    /// would read it as a formula (<c>= + - @</c>, a tab or a CR first), a property name for example.
    /// </summary>
    public static string Text(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        var text = value;
        if (text.Length > 0 && text[0] is '=' or '+' or '-' or '@' or '\t' or '\r')
            text = "'" + text;
        return text.IndexOfAny([Separator, '"', '\n', '\r']) >= 0
            ? "\"" + text.Replace("\"", "\"\"", StringComparison.Ordinal) + "\""
            : text;
    }
}
