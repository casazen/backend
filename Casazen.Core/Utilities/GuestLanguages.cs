using System.Globalization;

namespace Casazen.Core.Utilities;

/// <summary>
/// The languages of the guest check-in page and of its emails (NF-40, AC-NF40-4): Italian, English, German, French and Spanish. No language is
/// stored on the booking, so the language of a page is, in this order, the choice the guest saved on the session, the first supported language
/// of the browser, Italian.
/// </summary>
public static class GuestLanguages
{
    /// <summary>The language when nothing else is known.</summary>
    public const string Default = "it";

    /// <summary>The two-letter codes, in the order the page offers them.</summary>
    public static IReadOnlyList<string> Supported { get; } = ["it", "en", "de", "fr", "es"];

    /// <summary>A supported code (trimmed, lower case), or null for anything else: only the exact two-letter codes are accepted.</summary>
    public static string? Normalize(string? language)
    {
        if (string.IsNullOrWhiteSpace(language))
            return null;

        var value = language.Trim().ToLowerInvariant();
        return Supported.Contains(value, StringComparer.Ordinal) ? value : null;
    }

    /// <summary>
    /// The language of the page: <paramref name="sessionLanguage"/> when it is supported, otherwise the first supported language of the
    /// <c>Accept-Language</c> header in quality order (a tag counts by its primary language, <c>de-CH</c> is <c>de</c>), otherwise Italian.
    /// </summary>
    public static string Resolve(string? sessionLanguage, string? acceptLanguage)
    {
        if (Normalize(sessionLanguage) is { } chosen)
            return chosen;

        foreach (var tag in BrowserLanguages(acceptLanguage))
        {
            if (Normalize(tag) is { } language)
                return language;
        }

        return Default;
    }

    /// <summary>The culture the emails of <paramref name="language"/> are written in (Italian for an unsupported one).</summary>
    public static CultureInfo CultureOf(string? language) => Normalize(language) switch
    {
        "en" => CultureInfo.GetCultureInfo("en"),
        "de" => CultureInfo.GetCultureInfo("de"),
        "fr" => CultureInfo.GetCultureInfo("fr"),
        "es" => CultureInfo.GetCultureInfo("es"),
        _ => CultureInfo.GetCultureInfo("it-IT"),
    };

    /// <summary>
    /// The primary languages of an <c>Accept-Language</c> header by descending quality (the order of the header between equals); the ones
    /// with quality 0 and the wildcard are left out. A malformed part is skipped, never an error.
    /// </summary>
    private static IEnumerable<string> BrowserLanguages(string? acceptLanguage)
    {
        if (string.IsNullOrWhiteSpace(acceptLanguage))
            return [];

        var ranked = new List<(string Language, double Quality, int Order)>();
        var parts = acceptLanguage.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        for (var order = 0; order < parts.Length; order++)
        {
            var pieces = parts[order].Split(';', StringSplitOptions.TrimEntries);
            var tag = pieces[0];
            if (tag.Length == 0 || tag == "*")
                continue;

            var quality = 1.0;
            foreach (var parameter in pieces.Skip(1))
            {
                if (parameter.StartsWith("q=", StringComparison.OrdinalIgnoreCase)
                    && !double.TryParse(parameter.AsSpan(2), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out quality))
                {
                    quality = 0;
                }
            }

            if (quality <= 0)
                continue;

            var primary = tag.Split('-', '_')[0];
            ranked.Add((primary, quality, order));
        }

        return ranked.OrderByDescending(r => r.Quality).ThenBy(r => r.Order).Select(r => r.Language);
    }
}
