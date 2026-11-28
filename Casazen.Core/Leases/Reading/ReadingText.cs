using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Casazen.Core.Leases.Reading;

internal static partial class ReadingText
{
    private const int Timeout = 2000;

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

    public static string Compact(string? text) => Normalize(text).Replace(" ", string.Empty, StringComparison.Ordinal);

    public static int Letters(string? text) => string.IsNullOrEmpty(text) ? 0 : text.Count(char.IsLetterOrDigit);

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

    public static bool HasWords(string normalized, string words) =>
        $" {normalized} ".Contains($" {words} ", StringComparison.Ordinal);

    public const string AmountPattern = @"\d{1,3}(?:[. ]\d{3})+(?:,\d{1,2})?|\d+(?:[.,]\d{1,2})?";

    public static decimal? Amount(string raw) => LeaseContractTextParser.ParseAmount(raw);

    public const string DatePattern = @"\d{1,2}\s?[/.\-]\s?\d{1,2}\s?[/.\-]\s?(?:\d{4}|\d{2})";

    [GeneratedRegex(@"^(?<d>\d{1,2})\s?[/.\-]\s?(?<m>\d{1,2})\s?[/.\-]\s?(?<y>\d{4}|\d{2})$", RegexOptions.CultureInvariant, Timeout)]
    private static partial Regex NumericDate();

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
