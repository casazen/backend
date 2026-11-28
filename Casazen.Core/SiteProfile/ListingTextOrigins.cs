using Casazen.Core.Entities.Enums;

namespace Casazen.Core.SiteProfile;

/// <summary>
/// The origin of the texts of a listing (NF-31, ADR-035-R3): who wrote each text of <see cref="SitePropertyProfileDocument.Translations"/>
/// and the two rules that follow from it. A person's text is <see cref="TextOrigin.UserWritten"/>, or
/// <see cref="TextOrigin.ModifiedAfterTranslation"/> once the person has edited a machine translation; a machine translation is only ever written
/// where nothing is, or over an earlier machine translation, so it never overwrites what a person wrote.
/// </summary>
public static class ListingTextOrigins
{
    public static TextOrigin OriginOf(SitePropertyProfileDocument document, string language, ListingTextField field)
    {
        ArgumentNullException.ThrowIfNull(document);
        return document.Origins.TryGetValue(ListingTextResolver.Key(language, field), out var origin) ? origin : TextOrigin.UserWritten;
    }

    public static IReadOnlyDictionary<string, TextOrigin> Effective(SitePropertyProfileDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);

        var origins = new SortedDictionary<string, TextOrigin>(StringComparer.Ordinal);
        foreach (var (language, texts) in document.Translations)
        {
            foreach (var field in ListingTextResolver.Fields)
            {
                if (!string.IsNullOrWhiteSpace(texts.Get(field)))
                    origins[ListingTextResolver.Key(language, field)] = OriginOf(document, language, field);
            }
        }

        return origins;
    }

    public static TextOrigin WhenPersonSaves(string text, string? previousText, TextOrigin previousOrigin)
    {
        if (string.IsNullOrWhiteSpace(previousText))
            return TextOrigin.UserWritten;

        if (string.Equals(previousText, text, StringComparison.Ordinal))
            return previousOrigin;

        return previousOrigin == TextOrigin.UserWritten ? TextOrigin.UserWritten : TextOrigin.ModifiedAfterTranslation;
    }

    public static bool MachineMayWrite(string? previousText, TextOrigin previousOrigin) =>
        string.IsNullOrWhiteSpace(previousText) || previousOrigin == TextOrigin.AutoTranslated;
}
