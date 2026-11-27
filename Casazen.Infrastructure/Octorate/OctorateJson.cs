using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Casazen.Infrastructure.Octorate;

/// <summary>
/// Reads the members of an Octorate answer without regard to case and without ever throwing on a shape it does not know (E02/F02/US01):
/// a member that is missing, or of another kind, is <c>null</c>. The readers of the reservations and of the calendar are built on it. Nothing
/// here logs: the values are personal data or amounts.
/// </summary>
internal static partial class OctorateJson
{
    /// <summary>The member called one of <paramref name="names"/>, exact spelling first, then without regard to case.</summary>
    internal static JsonElement? Find(JsonElement element, params string[] names)
    {
        if (element.ValueKind != JsonValueKind.Object)
            return null;

        foreach (var name in names)
        {
            if (element.TryGetProperty(name, out var exact))
                return exact;
        }

        foreach (var property in element.EnumerateObject())
        {
            foreach (var name in names)
            {
                if (string.Equals(name, property.Name, StringComparison.OrdinalIgnoreCase))
                    return property.Value;
            }
        }

        return null;
    }

    /// <summary>A string, or a number as its text, trimmed; <c>null</c> when absent, blank or of another kind.</summary>
    internal static string? Text(JsonElement element, params string[] names) =>
        Find(element, names) is { } value ? TextOf(value) : null;

    internal static string? TextOf(JsonElement value)
    {
        var text = value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.GetRawText(),
            _ => null,
        };
        return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
    }

    /// <summary>A whole number from a JSON number or a numeric string that fits an <see cref="int"/>.</summary>
    internal static int? Int(JsonElement element, params string[] names)
    {
        if (Find(element, names) is not { } value || !OctorateHttp.TryReadLong(value, out var number))
            return null;

        return number is >= int.MinValue and <= int.MaxValue ? (int)number : null;
    }

    /// <summary>An amount from a JSON number or a numeric string (invariant culture).</summary>
    internal static decimal? Decimal(JsonElement element, params string[] names)
    {
        if (Find(element, names) is not { } value)
            return null;

        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetDecimal(out var number) => number,
            JsonValueKind.String when decimal.TryParse(value.GetString(), NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed) => parsed,
            _ => null,
        };
    }

    /// <summary>A boolean from <c>true</c>/<c>false</c>, the strings <c>"true"</c>/<c>"false"</c> or the numbers 1/0.</summary>
    internal static bool? Bool(JsonElement element, params string[] names)
    {
        if (Find(element, names) is not { } value)
            return null;

        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String when bool.TryParse(value.GetString(), out var parsed) => parsed,
            JsonValueKind.Number when value.TryGetInt32(out var number) && number is 0 or 1 => number == 1,
            _ => null,
        };
    }

    /// <summary>
    /// An instant in UTC from an ISO 8601 text (a text without an offset counts as UTC) or from a Unix time in seconds or milliseconds;
    /// <c>null</c> when absent or unreadable.
    /// </summary>
    internal static DateTime? Instant(JsonElement element, params string[] names)
    {
        if (Find(element, names) is not { } value)
            return null;

        if (value.ValueKind == JsonValueKind.Number)
            return value.TryGetInt64(out var epoch) ? FromUnixTime(epoch) : null;

        var text = TextOf(value);
        if (text is null)
            return null;

        if (text.Length >= 10 && text.All(char.IsAsciiDigit) && long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var number))
            return FromUnixTime(number);

        return DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed)
            ? parsed.UtcDateTime
            : null;
    }

    /// <summary>The calendar date at the start of <paramref name="text"/> (<c>2026-11-10</c> or <c>2026-11-10T00:00:00Z</c>); <c>null</c> when it does not start with one.</summary>
    internal static DateOnly? DateOf(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        var trimmed = text.Trim();
        return DatePrefix().IsMatch(trimmed)
               && DateOnly.TryParseExact(trimmed[..10], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? date
            : null;
    }

    private static DateTime? FromUnixTime(long value)
    {
        try
        {
            // Ten digits is seconds, thirteen is milliseconds.
            return (value > 99_999_999_999L ? DateTimeOffset.FromUnixTimeMilliseconds(value) : DateTimeOffset.FromUnixTimeSeconds(value)).UtcDateTime;
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    [GeneratedRegex(@"^\d{4}-\d{2}-\d{2}", RegexOptions.CultureInvariant)]
    private static partial Regex DatePrefix();
}
