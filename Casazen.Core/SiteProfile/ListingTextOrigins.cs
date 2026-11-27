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
    /// <summary>The origin the document records for a text; a text with no entry was written by a person.</summary>
    public static TextOrigin OriginOf(SitePropertyProfileDocument document, string language, ListingTextField field)
    {
        ArgumentNullException.ThrowIfNull(document);
        return document.Origins.TryGetValue(ListingTextResolver.Key(language, field), out var origin) ? origin : TextOrigin.UserWritten;
    }

    /// <summary>The origin of every text of the document, a person's where the document records none: what the editor reads.</summary>
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

    /// <summary>
    /// The origin of a text a person saves. A new text, or one the person changed from a person's, is <see cref="TextOrigin.UserWritten"/>;
    /// one left as it was keeps its origin; one the person changed from a machine translation (or from a text already modified after it) is
    /// <see cref="TextOrigin.ModifiedAfterTranslation"/>.
    /// </summary>
    /// <param name="text">The text being saved.</param>
    /// <param name="previousText">The text stored before, <c>null</c> when there was none.</param>
    /// <param name="previousOrigin">The origin stored with it (<see cref="TextOrigin.UserWritten"/> when none was recorded).</param>
    public static TextOrigin WhenPersonSaves(string text, string? previousText, TextOrigin previousOrigin)
    {
        if (string.IsNullOrWhiteSpace(previousText))
            return TextOrigin.UserWritten;

        if (string.Equals(previousText, text, StringComparison.Ordinal))
            return previousOrigin;

        return previousOrigin == TextOrigin.UserWritten ? TextOrigin.UserWritten : TextOrigin.ModifiedAfterTranslation;
    }

    /// <summary>
    /// A machine translation may write only where there is no text, or over an earlier machine translation: a text a person wrote, or edited
    /// after a translation, is never overwritten (NF-31).
    /// </summary>
    public static bool MachineMayWrite(string? previousText, TextOrigin previousOrigin) =>
        string.IsNullOrWhiteSpace(previousText) || previousOrigin == TextOrigin.AutoTranslated;
}
