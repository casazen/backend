using Casazen.Core.Entities;
using Casazen.Core.SiteProfile;

namespace Casazen.Infrastructure.Octorate;

/// <summary>
/// The languages of a listing as the channel manager has them (NF-31, ADR-035): IT, EN, FR, ES, DE, RU, PT, NL, JA, EL, TR, ZH, CA, RO. A
/// listing can be published when its headline and its description exist in Italian and in English; every other language is optional.
/// The texts themselves are read through <see cref="ListingTextResolver"/>, the only reader: this class only spells the languages the way the
/// channel manager does and says what is missing.
/// </summary>
public static class LanguageMapper
{
    /// <summary>The languages the listing must be written in to be published.</summary>
    public static IReadOnlyList<string> RequiredLanguages { get; } = [ListingLanguages.Italian, ListingLanguages.English];

    /// <summary>The fields that must exist in <see cref="RequiredLanguages"/>.</summary>
    public static IReadOnlyList<ListingTextField> RequiredFields { get; } = [ListingTextField.Title, ListingTextField.Description];

    /// <summary>Every language of the channel manager, as it spells them (upper case).</summary>
    public static IReadOnlyList<string> OctorateLanguages { get; } = [.. ListingLanguages.All.Select(code => code.ToUpperInvariant())];

    /// <summary>The code of the channel manager for a language of the listing: <c>fr</c> is <c>FR</c>.</summary>
    /// <exception cref="ArgumentException">The language is not one of <see cref="ListingLanguages.All"/>.</exception>
    public static string ToOctorate(string language) =>
        ListingLanguages.Normalize(language)?.ToUpperInvariant()
        ?? throw new ArgumentException("Not a language of the listing.", nameof(language));

    /// <summary>The language of the listing for a code of the channel manager (<c>FR</c> is <c>fr</c>); <c>null</c> when it is not one of the 14.</summary>
    public static string? FromOctorate(string? code) => ListingLanguages.Normalize(code);

    /// <summary>
    /// The texts a publication is still missing: the headline and the description in Italian and in English, as <c>language.field</c>
    /// (<c>en.title</c>, <c>en.description</c>), in that order; empty when the listing can be published. The publication answers 422
    /// <c>listing_content_incomplete</c> with this list and the status of the listing shows it.
    /// </summary>
    public static IReadOnlyList<string> ValidateForPublish(Property property)
    {
        ArgumentNullException.ThrowIfNull(property);
        return ValidateForPublish(property, SitePropertyProfileJson.Parse(property.SiteProfileJson));
    }

    /// <summary>The same over a profile already read.</summary>
    public static IReadOnlyList<string> ValidateForPublish(Property property, SitePropertyProfileDocument document)
    {
        ArgumentNullException.ThrowIfNull(property);
        ArgumentNullException.ThrowIfNull(document);

        var missing = new List<string>();
        foreach (var language in RequiredLanguages)
        {
            foreach (var field in RequiredFields)
            {
                if (ListingTextResolver.Get(property, document, language, field) is null)
                    missing.Add(ListingTextResolver.Key(language, field));
            }
        }

        return missing;
    }

    /// <summary>
    /// The language map of the channel manager for one field (<c>{ "IT": "...", "EN": "..." }</c>): every language the host wrote it in, keyed by
    /// the code the channel manager uses. Empty when no language has a text.
    /// </summary>
    public static IReadOnlyDictionary<string, string> LanguageMap(Property property, SitePropertyProfileDocument document, ListingTextField field)
    {
        ArgumentNullException.ThrowIfNull(property);
        ArgumentNullException.ThrowIfNull(document);

        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var language in ListingLanguages.All)
        {
            if (ListingTextResolver.Get(property, document, language, field) is { } text)
                map[ToOctorate(language)] = text;
        }

        return map;
    }
}
