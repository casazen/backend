using System.Globalization;

namespace Casazen.Core.TouristTax;

/// <summary>
/// Yearly season of a tourist tax rate: first and last day as <c>MM-dd</c>, both included, repeated every year
/// (<see cref="Entities.TouristTaxRate.SeasonStart"/>, <see cref="Entities.TouristTaxRate.SeasonEnd"/>).
/// A season may wrap over the new year (<c>11-01</c>..<c>02-28</c>). No season means the whole year.
/// </summary>
public static class TouristTaxSeason
{
    /// <summary>True for a valid <c>MM-dd</c> day (29 February included).</summary>
    public static bool IsValid(string? value) => TryParse(value, out _);

    /// <summary>
    /// True when both bounds are missing, or both are valid. A season with a single bound is not valid.
    /// </summary>
    public static bool IsValidPair(string? start, string? end)
    {
        var hasStart = !string.IsNullOrWhiteSpace(start);
        var hasEnd = !string.IsNullOrWhiteSpace(end);
        if (!hasStart && !hasEnd)
            return true;

        return hasStart && hasEnd && IsValid(start) && IsValid(end);
    }

    /// <summary>
    /// True when <paramref name="date"/> falls in the season. No season: always true. An invalid season: always false
    /// (the rate is never applied rather than applied all year).
    /// </summary>
    public static bool Contains(string? start, string? end, DateOnly date)
    {
        if (string.IsNullOrWhiteSpace(start) && string.IsNullOrWhiteSpace(end))
            return true;

        if (!TryParse(start, out var from) || !TryParse(end, out var to))
            return false;

        var day = (date.Month * 100) + date.Day;
        return from <= to
            ? day >= from && day <= to
            : day >= from || day <= to;
    }

    /// <summary>Parses <c>MM-dd</c> into <c>month * 100 + day</c>.</summary>
    private static bool TryParse(string? value, out int monthDay)
    {
        monthDay = 0;
        var text = value?.Trim();
        if (text is not { Length: 5 } || text[2] != '-')
            return false;

        if (!int.TryParse(text.AsSpan(0, 2), NumberStyles.None, CultureInfo.InvariantCulture, out var month)
            || !int.TryParse(text.AsSpan(3, 2), NumberStyles.None, CultureInfo.InvariantCulture, out var day))
            return false;

        // A leap year, so that 02-29 is a valid bound.
        if (month is < 1 or > 12 || day < 1 || day > DateTime.DaysInMonth(2024, month))
            return false;

        monthDay = (month * 100) + day;
        return true;
    }
}
