namespace Casazen.Core.SiteProfile;

/// <summary>
/// The languages the texts of a listing can be written in (NF-31, ADR-035): the ones of the channel manager, as lowercase ISO 639-1 codes.
/// Italian and English are mandatory to publish, the others are optional. The channel manager spells them in upper case (<c>IT</c>,
/// <c>EN</c>...): that is its mapper's business (<c>LanguageMapper</c>); here the code is the one the document stores.
/// </summary>
public static class ListingLanguages
{
    public const string Italian = "it";

    public const string English = "en";

    /// <summary>Every language of the listing: Italian, English, French, Spanish, German, Russian, Portuguese, Dutch, Japanese, Greek, Turkish, Chinese, Catalan, Romanian.</summary>
    public static IReadOnlyList<string> All { get; } =
        [Italian, English, "fr", "es", "de", "ru", "pt", "nl", "ja", "el", "tr", "zh", "ca", "ro"];

    /// <summary>The languages of the listing but Italian and English: the ones the profile texts keep in <see cref="LocalizedText.Others"/>.</summary>
    public static IReadOnlyList<string> Others { get; } = [.. All.Where(code => code is not Italian and not English)];

    /// <summary>The language code as the document stores it (trimmed, lower case), <c>null</c> when it is not one of <see cref="All"/>.</summary>
    public static string? Normalize(string? code)
    {
        var normalized = code?.Trim().ToLowerInvariant();
        return normalized is not null && All.Contains(normalized, StringComparer.Ordinal) ? normalized : null;
    }

    public static bool IsSupported(string? code) => Normalize(code) is not null;
}
