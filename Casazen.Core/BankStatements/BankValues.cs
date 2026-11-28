using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Casazen.Core.BankStatements;

internal readonly record struct Cell(string Text, bool IsNumber)
{
    public static readonly Cell Empty = new(string.Empty, false);

    public bool IsBlank => string.IsNullOrWhiteSpace(Text);
}

internal enum DecimalMark
{
    Comma = 0,
    Point = 1,
}

internal readonly record struct MonthReference(int Month, int? Year);

internal static partial class BankValues
{
    private const int RegexTimeoutMs = 1000;

    private static readonly string[] DateFormats =
    [
        "dd/MM/yyyy", "d/M/yyyy", "dd-MM-yyyy", "d-M-yyyy", "dd.MM.yyyy", "d.M.yyyy",
        "yyyy-MM-dd", "yyyy/MM/dd", "yyyy.MM.dd", "yyyyMMdd",
        "dd/MM/yy", "d/M/yy", "dd-MM-yy", "dd.MM.yy",
        "dd/MM/yyyy HH:mm", "dd/MM/yyyy HH:mm:ss", "d/M/yyyy H:mm", "d/M/yyyy H:mm:ss",
        "dd-MM-yyyy HH:mm:ss", "yyyy-MM-dd HH:mm:ss", "yyyy-MM-dd HH:mm", "yyyy-MM-ddTHH:mm:ss", "yyyy-MM-ddTHH:mm:ssK",
        "yyyy-MM-ddTHH:mm:ss.FFFFFFFK", "yyyy-MM-ddK",
    ];

    private const int MinYear = 2000;
    private const int MaxYear = 2100;

    private static readonly Dictionary<string, int> FullMonths = MonthTable(
        "gennaio febbraio marzo aprile maggio giugno luglio agosto settembre ottobre novembre dicembre",
        "january february march april may june july august september october november december");

    private static readonly Dictionary<string, int> ShortMonths = MonthTable(
        "gen feb mar apr mag giu lug ago set|sett ott nov dic",
        "jan feb mar apr may jun jul aug sep oct nov dec");

    private static readonly HashSet<string> BeforeMonthWords = new(
        ["mese", "mensilita", "di", "del", "per", "canone", "affitto", "locazione", "rata", "pigione", "month"],
        StringComparer.Ordinal);

    private static readonly Dictionary<string, int> IbanLengths = CountryLengths(
        "IT27 SM27 VA22 CH21 LI21 DE22 FR27 ES24 GB22 NL18 BE16 AT20 LU20 PT25 IE22 MC27 MT31 RO24");

    private static Dictionary<string, int> MonthTable(params string[] lists)
    {
        var table = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var list in lists)
        {
            var slots = list.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            for (var month = 1; month <= slots.Length; month++)
            {
                foreach (var name in slots[month - 1].Split('|'))
                    table[name] = month;
            }
        }

        return table;
    }

    private static Dictionary<string, int> CountryLengths(string list) =>
        list.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .ToDictionary(entry => entry[..2], entry => int.Parse(entry[2..], CultureInfo.InvariantCulture), StringComparer.Ordinal);

    [GeneratedRegex(@"\d,\d{1,2}(?!\d)\D*$", RegexOptions.CultureInvariant, RegexTimeoutMs)]
    private static partial Regex CommaDecimalTail();

    [GeneratedRegex(@"\d\.\d{1,2}(?!\d)\D*$", RegexOptions.CultureInvariant, RegexTimeoutMs)]
    private static partial Regex PointDecimalTail();

    [GeneratedRegex(@"(?<![A-Z0-9])[A-Z]{2}\d{2}(?: ?[A-Z0-9]{4}){2,7}(?: ?[A-Z0-9]{1,3})?(?![A-Z0-9])", RegexOptions.CultureInvariant, RegexTimeoutMs)]
    private static partial Regex IbanCandidate();

    /// <summary>
    /// Lower case, no accents, every run of anything that is not a letter or a digit one space: how two writings of the same name or address
    /// are compared. <c>D'Angelo</c> is <c>d angelo</c>, <c>Perché</c> is <c>perche</c>.
    /// </summary>
    public static string NormalizeText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        var decomposed = value.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);
        var pendingSpace = false;
        foreach (var ch in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark)
                continue;

            if (char.IsLetterOrDigit(ch))
            {
                if (pendingSpace && builder.Length > 0)
                    builder.Append(' ');
                builder.Append(char.ToLowerInvariant(ch));
                pendingSpace = false;
            }
            else
            {
                pendingSpace = true;
            }
        }

        return builder.ToString();
    }

    public static string Clip(string? value, int max = BankStatementLimits.MaxTextLength)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;
        var text = value.Trim();
        if (text.Length <= max)
            return text;
        var cut = max;
        if (char.IsHighSurrogate(text[cut - 1]))
            cut--;
        return text[..cut];
    }

    public static string JoinDistinct(IEnumerable<string?> parts)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var builder = new StringBuilder();
        foreach (var part in parts)
        {
            var text = part?.Trim();
            if (string.IsNullOrEmpty(text) || !seen.Add(text))
                continue;
            if (builder.Length > 0)
                builder.Append(' ');
            builder.Append(text);
        }

        return Clip(builder.ToString());
    }

    public static DecimalMark DetectDecimalMark(IEnumerable<Cell> cells)
    {
        int comma = 0, point = 0;
        foreach (var cell in cells)
        {
            if (cell.IsNumber)
                continue;
            var text = cell.Text.Trim();
            if (CommaDecimalTail().IsMatch(text))
                comma++;
            else if (PointDecimalTail().IsMatch(text))
                point++;
        }

        return point > comma ? DecimalMark.Point : DecimalMark.Comma;
    }

    public static bool TryParseAmount(Cell cell, DecimalMark mark, out decimal amount)
    {
        amount = 0m;
        if (cell.IsBlank)
            return false;

        if (cell.IsNumber)
        {
            if (!decimal.TryParse(cell.Text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
                return false;
            amount = decimal.Round(number, 2, MidpointRounding.AwayFromZero);
            return true;
        }

        var negative = false;
        var digits = new StringBuilder();
        var letters = new StringBuilder();
        var text = cell.Text.Trim();
        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            if (char.IsAsciiDigit(ch) || ch is '.' or ',')
            {
                digits.Append(ch);
            }
            else if (ch is '-' or '−' or '–')
            {
                negative = true;
            }
            else if (ch == '(' || ch == ')')
            {
                negative = true;
            }
            else if (char.IsLetter(ch))
            {
                letters.Append(char.ToUpperInvariant(ch));
            }
            else if (ch is '+' or '€' or '$' or '£' || char.IsWhiteSpace(ch))
            {
            }
            else
            {
                return false;
            }
        }

        var suffix = letters.ToString();
        if (suffix.Length > 0)
        {
            switch (suffix)
            {
                case "EUR" or "EURO" or "EUROS":
                    break;
                case "CR" or "C" or "AVERE":
                    break;
                case "DR" or "DB" or "D" or "DARE":
                    negative = true;
                    break;
                default:
                    return false;
            }
        }

        if (!TryReadDigits(digits.ToString(), mark, out var value))
            return false;

        amount = decimal.Round(negative ? -value : value, 2, MidpointRounding.AwayFromZero);
        return true;
    }

    private static bool TryReadDigits(string raw, DecimalMark mark, out decimal value)
    {
        value = 0m;
        if (raw.Length == 0 || !raw.Any(char.IsDigit))
            return false;

        var lastComma = raw.LastIndexOf(',');
        var lastPoint = raw.LastIndexOf('.');
        string normalized;
        if (lastComma >= 0 && lastPoint >= 0)
        {
            var decimalAt = Math.Max(lastComma, lastPoint);
            var whole = raw[..decimalAt].Replace(".", string.Empty, StringComparison.Ordinal).Replace(",", string.Empty, StringComparison.Ordinal);
            normalized = $"{whole}.{raw[(decimalAt + 1)..]}";
        }
        else if (lastComma >= 0 || lastPoint >= 0)
        {
            var separator = lastComma >= 0 ? ',' : '.';
            var count = raw.Count(c => c == separator);
            var at = raw.LastIndexOf(separator);
            var after = raw.Length - at - 1;
            var isDecimal = count == 1 && (separator == ',' ? mark == DecimalMark.Comma : mark == DecimalMark.Point);
            if (count == 1 && !isDecimal)
            {
                isDecimal = after != 3;
            }

            normalized = isDecimal
                ? $"{raw[..at].Replace(".", string.Empty, StringComparison.Ordinal).Replace(",", string.Empty, StringComparison.Ordinal)}.{raw[(at + 1)..]}"
                : raw.Replace(".", string.Empty, StringComparison.Ordinal).Replace(",", string.Empty, StringComparison.Ordinal);
        }
        else
        {
            normalized = raw;
        }

        if (normalized.StartsWith('.'))
            normalized = "0" + normalized;
        if (normalized.EndsWith('.'))
            normalized = normalized[..^1];
        return decimal.TryParse(normalized, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out value);
    }

    public static bool TryParseDate(Cell cell, out DateOnly date)
    {
        date = default;
        if (cell.IsBlank)
            return false;

        var text = cell.Text.Trim();
        if (cell.IsNumber || IsSerialLike(text))
        {
            if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var serial) || serial < 36526 || serial > 73415)
                return false;
            date = DateOnly.FromDateTime(DateTime.FromOADate(Math.Floor(serial)));
            return true;
        }

        if (DateTimeOffset.TryParseExact(text, DateFormats, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.AssumeUniversal, out var parsed))
        {
            if (parsed.Year < MinYear || parsed.Year > MaxYear)
                return false;
            date = DateOnly.FromDateTime(parsed.DateTime);
            return true;
        }

        return TryParseTextualDate(text, out date);
    }

    private static bool TryParseTextualDate(string text, out DateOnly date)
    {
        date = default;
        var parts = text.Split([' ', '-', '/', '.'], StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 3
            || !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var day)
            || !int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var year))
        {
            return false;
        }

        var name = NormalizeText(parts[1]);
        if (!FullMonths.TryGetValue(name, out var month) && !ShortMonths.TryGetValue(name, out month))
            return false;

        if (parts[2].Length == 2)
            year += 2000;
        if (year is < MinYear or > MaxYear || day < 1 || day > DateTime.DaysInMonth(year, month))
            return false;

        date = new DateOnly(year, month, day);
        return true;
    }

    private static bool IsSerialLike(string text) =>
        text.Length is 5 && text.All(char.IsAsciiDigit);

    public static string? NormalizeIban(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        var compact = new string(value.Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();
        return IsValidIban(compact) ? compact : null;
    }

    public static string? FindIban(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return null;

        foreach (Match match in IbanCandidate().Matches(text.ToUpperInvariant()))
        {
            var compact = match.Value.Replace(" ", string.Empty, StringComparison.Ordinal);
            for (var length = Math.Min(34, compact.Length); length >= 15; length--)
            {
                var candidate = compact[..length];
                if (IsValidIban(candidate))
                    return candidate;
            }
        }

        return null;
    }

    public static bool IsValidIban(string iban)
    {
        if (iban.Length is < 15 or > 34)
            return false;
        if (!char.IsAsciiLetterUpper(iban[0]) || !char.IsAsciiLetterUpper(iban[1]) || !char.IsAsciiDigit(iban[2]) || !char.IsAsciiDigit(iban[3]))
            return false;
        if (IbanLengths.TryGetValue(iban[..2], out var expected) && iban.Length != expected)
            return false;

        var remainder = 0;
        for (var i = 4; i < iban.Length + 4; i++)
        {
            var ch = iban[i % iban.Length];
            if (char.IsAsciiDigit(ch))
            {
                remainder = ((remainder * 10) + (ch - '0')) % 97;
            }
            else if (char.IsAsciiLetterUpper(ch))
            {
                remainder = ((remainder * 100) + (ch - 'A' + 10)) % 97;
            }
            else
            {
                return false;
            }
        }

        return remainder == 1;
    }

    public static string NormalizeCurrency(string? value)
    {
        var text = value?.Trim().ToUpperInvariant();
        return string.IsNullOrEmpty(text) || text is "€" or "EURO" or "EUROS" ? "EUR" : Clip(text, 8);
    }

    public static IReadOnlyList<MonthReference> ExtractMonths(string normalizedText)
    {
        if (string.IsNullOrEmpty(normalizedText))
            return [];

        var tokens = normalizedText.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var found = new List<MonthReference>();

        bool IsDay(int index) => index >= 0 && tokens[index].Length is 1 or 2 && int.TryParse(tokens[index], NumberStyles.None, CultureInfo.InvariantCulture, out var day) && day is >= 1 and <= 31;

        int? YearAt(int index)
        {
            if (index >= tokens.Length)
                return null;
            return tokens[index].Length == 4
                && int.TryParse(tokens[index], NumberStyles.None, CultureInfo.InvariantCulture, out var year)
                && year is >= MinYear and <= MaxYear
                    ? year
                    : null;
        }

        for (var i = 0; i < tokens.Length; i++)
        {
            var token = tokens[i];
            if (FullMonths.TryGetValue(token, out var fullMonth))
            {
                if (!IsDay(i - 1))
                    found.Add(new MonthReference(fullMonth, YearAt(i + 1)));
                continue;
            }

            if (ShortMonths.TryGetValue(token, out var shortMonth))
            {
                var year = YearAt(i + 1);
                var introduced = i > 0 && BeforeMonthWords.Contains(tokens[i - 1]);
                if ((year is not null || introduced) && !IsDay(i - 1))
                    found.Add(new MonthReference(shortMonth, year));
                continue;
            }

            if (token.Length is 1 or 2 && int.TryParse(token, NumberStyles.None, CultureInfo.InvariantCulture, out var month) && month is >= 1 and <= 12)
            {
                var year = YearAt(i + 1);
                if (year is not null && !IsDay(i - 1))
                    found.Add(new MonthReference(month, year));
            }
            else if (YearAt(i) is { } leadingYear
                && i + 1 < tokens.Length
                && tokens[i + 1].Length is 1 or 2
                && int.TryParse(tokens[i + 1], NumberStyles.None, CultureInfo.InvariantCulture, out var yearMonth)
                && yearMonth is >= 1 and <= 12
                && !(i + 2 < tokens.Length && IsDay(i + 2)))
            {
                found.Add(new MonthReference(yearMonth, leadingYear));
            }
        }

        return found.Distinct().ToList();
    }

    public static bool ReferencesPeriod(IReadOnlyList<MonthReference> references, DateOnly start, DateOnly end)
    {
        if (references.Count == 0)
            return false;

        var cursor = new DateOnly(start.Year, start.Month, 1);
        var last = new DateOnly(end.Year, end.Month, 1);
        for (var guard = 0; cursor <= last && guard < 24; guard++)
        {
            if (references.Any(r => r.Month == cursor.Month && (r.Year is null || r.Year == cursor.Year)))
                return true;
            cursor = cursor.AddMonths(1);
        }

        return false;
    }
}
