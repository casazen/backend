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

    /// <summary>
    /// The language to write to a guest by the country on the booking (E23/F01/US01/T01), when the guest chose none: Italian for Italy, German for Germany and Austria, French
    /// for France, Spanish for Spain and English for every other country; <c>null</c> when the booking names no country (a stay from a portal comes with none), so the caller
    /// decides. Accepts the two-letter and three-letter ISO codes and the usual names. What the review invitation, the chat message of the check-in link and the scheduled messages have always
    /// read, and still read while the guest portal is off: the e-mails of the portal read <see cref="OfUnambiguousCountry"/> instead.
    /// </summary>
    public static string? OfCountry(string? country) =>
        string.IsNullOrWhiteSpace(country) ? null : OfUnambiguousCountry(country) ?? "en";

    /// <summary>
    /// The language to write to a guest by the country on the booking in the e-mails of the guest portal (E50/F01/US03/T02), when the guest chose none: only a country whose language is not in doubt
    /// gives one — Italian for Italy, German for Germany and Austria, French for France, Spanish for Spain, English for the United Kingdom, the United States and Ireland. <c>null</c> for every other country
    /// (Switzerland, where four languages are spoken, included) and when the booking names none, so the caller goes on to the language of the host's account instead of writing English to every guest
    /// that happens to come from somewhere else. Accepts the two-letter and three-letter ISO codes and the usual names.
    /// </summary>
    public static string? OfUnambiguousCountry(string? country)
    {
        var value = country?.Trim().ToUpperInvariant();
        if (string.IsNullOrEmpty(value))
            return null;

        return value switch
        {
            "IT" or "ITA" or "ITALIA" or "ITALY" => "it",
            "DE" or "DEU" or "GERMANIA" or "GERMANY" or "DEUTSCHLAND" or "AT" or "AUT" or "AUSTRIA" or "ÖSTERREICH" => "de",
            "FR" or "FRA" or "FRANCIA" or "FRANCE" => "fr",
            "ES" or "ESP" or "SPAGNA" or "SPAIN" or "ESPAÑA" => "es",
            "GB" or "GBR" or "UK" or "REGNO UNITO" or "UNITED KINGDOM" => "en",
            "US" or "USA" or "STATI UNITI" or "UNITED STATES" => "en",
            "IE" or "IRL" or "IRLANDA" or "IRELAND" => "en",
            _ => null,
        };
    }

    /// <summary>
    /// The one choice of the language of an e-mail to a guest (E50/F01/US03/T02, NF-155, AC-NF155-3), in this order: the language the guest chose on the check-in page
    /// (<paramref name="sessionLanguage"/>, when it is supported), the language of the country on the booking when that country has one language beyond doubt
    /// (<see cref="OfUnambiguousCountry"/>), the language of the account of the host (<paramref name="orgDefaultLanguage"/>, <c>Org.DefaultLanguage</c>), Italian. Any other country, Switzerland
    /// included, leaves the choice to the host's account: it is never turned into English. A booking carries no language of its own, and the public booking requests carry none: a confirmation
    /// written before any check-in knows the guest only by its country, else by the host's account. An unsupported code at any step is skipped, so the result is always one of <see cref="Supported"/>.
    /// </summary>
    public static string ResolveForEmail(string? sessionLanguage, string? country, string? orgDefaultLanguage) =>
        Resolve(Normalize(sessionLanguage) ?? OfUnambiguousCountry(country), orgDefaultLanguage);

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
