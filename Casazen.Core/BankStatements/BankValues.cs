using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Casazen.Core.BankStatements;

/// <summary>One cell of a table: its text, and whether the file stored it as a number (Excel does, a CSV never does).</summary>
internal readonly record struct Cell(string Text, bool IsNumber)
{
    public static readonly Cell Empty = new(string.Empty, false);

    public bool IsBlank => string.IsNullOrWhiteSpace(Text);
}

/// <summary>Which character an amount uses for the decimals: the Italian banks write <c>1.234,56</c>, others <c>1,234.56</c>.</summary>
internal enum DecimalMark
{
    Comma = 0,
    Point = 1,
}

/// <summary>A month a payment reason names; the year is null when the text does not give one.</summary>
internal readonly record struct MonthReference(int Month, int? Year);

/// <summary>
/// Reading of the values of a bank file: amounts, dates, IBANs, currencies and the months a reason names. Each bank writes them its own way,
/// so the readers are tolerant in what they accept and strict in what they produce (cents, a real date, an IBAN that checks).
/// </summary>
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

    /// <summary>The months by their full name, in Italian and in English (the lists are the twelve months in order).</summary>
    private static readonly Dictionary<string, int> FullMonths = MonthTable(
        "gennaio febbraio marzo aprile maggio giugno luglio agosto settembre ottobre novembre dicembre",
        "january february march april may june july august september october november december");

    /// <summary>
    /// Abbreviations count only next to a year or a word that says «month of» (<c>set</c> and <c>ago</c> are also plain words). A month may
    /// have more than one, told apart by a bar.
    /// </summary>
    private static readonly Dictionary<string, int> ShortMonths = MonthTable(
        "gen feb mar apr mag giu lug ago set|sett ott nov dic",
        "jan feb mar apr may jun jul aug sep oct nov dec");

    private static readonly HashSet<string> BeforeMonthWords = new(
        ["mese", "mensilita", "di", "del", "per", "canone", "affitto", "locazione", "rata", "pigione", "month"],
        StringComparer.Ordinal);

    /// <summary>Lengths of the IBANs of the countries a rent may come from (country code and length); any other country is judged by its check digits alone.</summary>
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

    // ---------------------------------------------------------------- text

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

    /// <summary>Text cut to the longest the importer keeps, without breaking a surrogate pair.</summary>
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

    /// <summary>Joins the non-empty parts with a space, once each (a description repeated in two columns is not written twice).</summary>
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

    // ---------------------------------------------------------------- amounts

    /// <summary>
    /// Which decimal mark the amounts of a column use, from the cells that tell (<c>12,50</c> or <c>12.50</c>); the Italian one when none does
    /// (whole euros, or thousands only).
    /// </summary>
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

    /// <summary>
    /// An amount in cents with its sign. Accepts <c>1.234,56</c>, <c>1,234.56</c>, <c>-12,50</c>, <c>12,50-</c>, <c>(12,50)</c>, <c>€ 12,50</c>,
    /// <c>12,50 EUR</c>, <c>12,50 DR</c> (debit) and <c>12,50 CR</c> (credit); a number stored as such by Excel is taken as it is.
    /// </summary>
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
                // A minus before the figures or after them: both mean a debit.
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
                // Symbols and spaces carry no value.
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
            // Both marks: the one on the right is the decimal mark, the other groups the thousands.
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
                // The other bank's decimal mark: three digits after it are a group of thousands, anything else a decimal part.
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

    // ---------------------------------------------------------------- dates

    /// <summary>A date written the way Italian, ISO and Excel files write it; a number is an Excel serial. Years before 2000 and after 2100 are not dates of a rent.</summary>
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

        // A date with a time zone keeps the day it was written in (its own offset), it is never moved to UTC.
        if (DateTimeOffset.TryParseExact(text, DateFormats, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.AssumeUniversal, out var parsed))
        {
            if (parsed.Year < MinYear || parsed.Year > MaxYear)
                return false;
            date = DateOnly.FromDateTime(parsed.DateTime);
            return true;
        }

        return TryParseTextualDate(text, out date);
    }

    /// <summary>«3 ott 2026», «03 ottobre 2026», «3-ott-26»: the month by its name in Italian or English, read from our own table and not from the machine's culture data.</summary>
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

    // ---------------------------------------------------------------- IBAN and currency

    /// <summary>The IBAN of a field of its own: upper case, no spaces, only if the check digits are right.</summary>
    public static string? NormalizeIban(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        var compact = new string(value.Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();
        return IsValidIban(compact) ? compact : null;
    }

    /// <summary>The first valid IBAN written in a text, with or without the spaces banks put between its groups.</summary>
    public static string? FindIban(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return null;

        foreach (Match match in IbanCandidate().Matches(text.ToUpperInvariant()))
        {
            var compact = match.Value.Replace(" ", string.Empty, StringComparison.Ordinal);
            // The pattern may have swallowed the first letters of the next word: try the shorter ones too.
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

    /// <summary>ISO code in upper case; nothing, <c>€</c> and <c>Euro</c> are <c>EUR</c>.</summary>
    public static string NormalizeCurrency(string? value)
    {
        var text = value?.Trim().ToUpperInvariant();
        return string.IsNullOrEmpty(text) || text is "€" or "EURO" or "EUROS" ? "EUR" : Clip(text, 8);
    }

    // ---------------------------------------------------------------- months

    /// <summary>
    /// The months a payment reason names («canone ottobre 2026», «affitto 10/2026», «mese di set»). A month that is only part of a full date
    /// («del 3 ottobre 2026», «03/10/2026») is the day of the payment and not what the payment is for, so it is not a reference.
    /// </summary>
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

            // «10/2026» and «10-2026» arrive here as «10 2026»; «2026-10» as «2026 10».
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

    /// <summary>True when one of the references falls in the months <paramref name="start"/> to <paramref name="end"/> (both ends included).</summary>
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
