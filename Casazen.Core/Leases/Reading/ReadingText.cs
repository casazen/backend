using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Casazen.Core.Leases.Reading;

/// <summary>
/// The small tools the readers of a contract document share (CF-DOCS-REAL): the text brought to a plain form that an OCR's small
/// mistakes do not break, the search of a title that tolerates a few wrong letters, and the amounts and dates as an Italian document
/// writes them. Pure, no I/O, nothing logged.
/// </summary>
internal static partial class ReadingText
{
    private const int Timeout = 2000;

    /// <summary>Lower case, no accents, anything that is not a letter or a digit is one space: how a heading is compared.</summary>
    public static string Normalize(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return string.Empty;

        var builder = new StringBuilder(text.Length);
        var lastWasSpace = true;
        foreach (var raw in text.Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(raw) == UnicodeCategory.NonSpacingMark)
                continue;

            if (char.IsLetterOrDigit(raw))
            {
                builder.Append(char.ToLowerInvariant(raw));
                lastWasSpace = false;
            }
            else if (!lastWasSpace)
            {
                builder.Append(' ');
                lastWasSpace = true;
            }
        }

        return builder.ToString().Trim();
    }

    /// <summary><see cref="Normalize"/> without the spaces: a title the OCR split in two or glued to its neighbour is still found.</summary>
    public static string Compact(string? text) => Normalize(text).Replace(" ", string.Empty, StringComparison.Ordinal);

    /// <summary>The number of letters and digits of a text: what tells a page that holds text from one that is blank or unreadable.</summary>
    public static int Letters(string? text) => string.IsNullOrEmpty(text) ? 0 : text.Count(char.IsLetterOrDigit);

    /// <summary>
    /// The smallest number of wrong, missing or extra letters with which <paramref name="phrase"/> appears in <paramref name="text"/>
    /// (both compact), and where the best occurrence ends; the phrase is not found at all when it needs more than a sixth of its length.
    /// </summary>
    public static (bool Found, int End) FindLike(string text, string phrase)
    {
        if (phrase.Length == 0 || text.Length == 0)
            return (false, -1);

        var allowed = phrase.Length / 6;
        var previous = new int[phrase.Length + 1];
        var current = new int[phrase.Length + 1];
        for (var j = 0; j <= phrase.Length; j++)
            previous[j] = j;

        var best = int.MaxValue;
        var end = -1;
        for (var i = 1; i <= text.Length; i++)
        {
            current[0] = 0;
            for (var j = 1; j <= phrase.Length; j++)
            {
                var cost = text[i - 1] == phrase[j - 1] ? 0 : 1;
                current[j] = Math.Min(Math.Min(previous[j] + 1, current[j - 1] + 1), previous[j - 1] + cost);
            }

            if (current[phrase.Length] < best)
            {
                best = current[phrase.Length];
                end = i;
            }

            (previous, current) = (current, previous);
        }

        return best <= allowed ? (true, end) : (false, -1);
    }

    /// <summary>Whether the normalized text holds the words as a whole (not as part of a longer word).</summary>
    public static bool HasWords(string normalized, string words) =>
        $" {normalized} ".Contains($" {words} ", StringComparison.Ordinal);

    // ---- amounts ------------------------------------------------------------------------------------------------------------

    /// <summary>An amount of euros as the documents of the Agenzia and the contracts write it: "7.140,00", "595,00", "595".</summary>
    public const string AmountPattern = @"\d{1,3}(?:[. ]\d{3})+(?:,\d{1,2})?|\d+(?:[.,]\d{1,2})?";

    public static decimal? Amount(string raw) => LeaseContractTextParser.ParseAmount(raw);

    // ---- dates --------------------------------------------------------------------------------------------------------------

    /// <summary>A date as numbers, with a four or a two digit year: "19/09/2026", "19-9-26".</summary>
    public const string DatePattern = @"\d{1,2}\s?[/.\-]\s?\d{1,2}\s?[/.\-]\s?(?:\d{4}|\d{2})";

    [GeneratedRegex(@"^(?<d>\d{1,2})\s?[/.\-]\s?(?<m>\d{1,2})\s?[/.\-]\s?(?<y>\d{4}|\d{2})$", RegexOptions.CultureInvariant, Timeout)]
    private static partial Regex NumericDate();

    /// <summary>The calendar date written as numbers; a two digit year is read as 20xx. <c>null</c> when it is not a real day.</summary>
    public static DateOnly? Date(string raw)
    {
        var match = NumericDate().Match(raw.Trim());
        if (!match.Success)
            return null;

        var day = int.Parse(match.Groups["d"].Value, CultureInfo.InvariantCulture);
        var month = int.Parse(match.Groups["m"].Value, CultureInfo.InvariantCulture);
        var year = int.Parse(match.Groups["y"].Value, CultureInfo.InvariantCulture);
        if (year < 100)
            year += 2000;

        if (year is < 1970 or > 2100 || month is < 1 or > 12 || day < 1 || day > DateTime.DaysInMonth(year, month))
            return null;

        return new DateOnly(year, month, day);
    }
}
