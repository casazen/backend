using Casazen.Core.Entities;
using Casazen.Core.SiteProfile;

namespace Casazen.Infrastructure.Octorate;

public static class LanguageMapper
{
    public static IReadOnlyList<string> RequiredLanguages { get; } = [ListingLanguages.Italian, ListingLanguages.English];

    public static IReadOnlyList<ListingTextField> RequiredFields { get; } = [ListingTextField.Title, ListingTextField.Description];

    public static IReadOnlyList<string> OctorateLanguages { get; } = [.. ListingLanguages.All.Select(code => code.ToUpperInvariant())];

    public static string ToOctorate(string language) =>
        ListingLanguages.Normalize(language)?.ToUpperInvariant()
        ?? throw new ArgumentException("Not a language of the listing.", nameof(language));

    public static string? FromOctorate(string? code) => ListingLanguages.Normalize(code);

    public static IReadOnlyList<string> ValidateForPublish(Property property)
    {
        ArgumentNullException.ThrowIfNull(property);
        return ValidateForPublish(property, SitePropertyProfileJson.Parse(property.SiteProfileJson));
    }

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
