using Casazen.Core.Entities;

namespace Casazen.Core.SiteProfile;

/// <summary>
/// The seven text fields of a listing as the channel manager has them (NF-31, ADR-035). The numbers are not stored; the names of the
/// fields are, in lower case, in the keys of <see cref="SitePropertyProfileDocument.Origins"/> (<see cref="ListingTextResolver.CodeOf"/>).
/// </summary>
public enum ListingTextField
{
    Title = 0,
    Description = 1,

    /// <summary>The spaces of the house: the notes on the floor and on the outdoor spaces.</summary>
    Spaces = 2,

    /// <summary>How guests get in: the way check-in works.</summary>
    Access = 3,

    /// <summary>The surroundings: the zone and what is near.</summary>
    Neighborhood = 4,

    /// <summary>How to get there. It has no column and no profile text: Italian is kept in the document too.</summary>
    Transport = 5,

    /// <summary>The house rules and the note on them.</summary>
    Rules = 6,
}

/// <summary>
/// The only reader of the texts of a listing for a language and a field (NF-31, ADR-035): the channels push through it and the editor
/// reads through it, so the texts have no second store and no second rule.
/// </summary>
/// <remarks>
/// <para><b>Italian</b> is where it always was: the headline (<c>Property.ListingTitle</c>, the name of the property when the host wrote none),
/// <c>Property.Description</c> and <c>Property.HouseRules</c>, and the <see cref="LocalizedText.It"/> part of the profile texts. The fields built
/// from more than one text join them in this order, one on each line: <c>spaces</c> = floor note + outdoor note, <c>neighborhood</c> = zone +
/// near, <c>rules</c> = house rules + note on the rules. Only the transport has no other place than the document.</para>
/// <para><b>Any other language</b> comes from the document: the text written for that language and field (<see cref="ListingTexts"/>). For
/// the three fields that are only a join of profile texts (<c>spaces</c>, <c>access</c>, <c>neighborhood</c>) there is a fall back when none
/// was written: the same join over the English part (<see cref="LocalizedText.En"/>) or the part of that language
/// (<see cref="LocalizedText.Others"/>), so what the host already wrote for the page in English reaches the channels. The title, the
/// description, the transport and the rules have no fall back: the house rules column is Italian, and a half text would say less than the
/// host meant.</para>
/// </remarks>
public static class ListingTextResolver
{
    private const string Separator = "\n";

    public static IReadOnlyList<ListingTextField> Fields { get; } = Enum.GetValues<ListingTextField>();

    /// <summary>The name of the field in the channel manager and in the keys of the origins: <c>title</c>, <c>description</c>, <c>spaces</c>, <c>access</c>, <c>neighborhood</c>, <c>transport</c>, <c>rules</c>.</summary>
    public static string CodeOf(ListingTextField field) => field switch
    {
        ListingTextField.Title => "title",
        ListingTextField.Description => "description",
        ListingTextField.Spaces => "spaces",
        ListingTextField.Access => "access",
        ListingTextField.Neighborhood => "neighborhood",
        ListingTextField.Transport => "transport",
        ListingTextField.Rules => "rules",
        _ => throw new ArgumentOutOfRangeException(nameof(field), field, "Not a field of the listing."),
    };

    /// <summary>The field called <paramref name="code"/> (<see cref="CodeOf"/>, case does not matter); <c>false</c> when there is none.</summary>
    public static bool TryParseField(string? code, out ListingTextField field)
    {
        foreach (var candidate in Fields)
        {
            if (string.Equals(CodeOf(candidate), code?.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                field = candidate;
                return true;
            }
        }

        field = default;
        return false;
    }

    /// <summary>The key of a text in <see cref="SitePropertyProfileDocument.Origins"/> and in the lists of missing texts: <c>fr.title</c>.</summary>
    public static string Key(string language, ListingTextField field) => language + "." + CodeOf(field);

    /// <summary>
    /// The text of <paramref name="field"/> in <paramref name="language"/> (a code of <see cref="ListingLanguages.All"/>, case does not
    /// matter), read from the property and its stored profile; <c>null</c> when the host wrote none, or the language is not one of the listing.
    /// </summary>
    public static string? Get(Property property, string language, ListingTextField field)
    {
        ArgumentNullException.ThrowIfNull(property);
        return Get(property, SitePropertyProfileJson.Parse(property.SiteProfileJson), language, field);
    }

    /// <summary>The same, over a profile already read (a caller that asks for many texts reads the column once).</summary>
    public static string? Get(Property property, SitePropertyProfileDocument document, string language, ListingTextField field)
    {
        ArgumentNullException.ThrowIfNull(property);
        ArgumentNullException.ThrowIfNull(document);

        var code = ListingLanguages.Normalize(language);
        if (code is null)
            return null;

        return code == ListingLanguages.Italian
            ? ItalianText(property, document, field)
            : OtherText(document, code, field);
    }

    /// <summary>Every field the host wrote in <paramref name="language"/>, by field.</summary>
    public static IReadOnlyDictionary<ListingTextField, string> GetAll(Property property, SitePropertyProfileDocument document, string language)
    {
        var texts = new Dictionary<ListingTextField, string>();
        foreach (var field in Fields)
        {
            if (Get(property, document, language, field) is { } text)
                texts[field] = text;
        }

        return texts;
    }

    private static string? ItalianText(Property property, SitePropertyProfileDocument document, ListingTextField field) => field switch
    {
        ListingTextField.Title => Clean(property.ListingTitle) ?? Clean(property.Name),
        ListingTextField.Description => Clean(property.Description),
        ListingTextField.Spaces => Join(document.FloorNote?.It, document.OutdoorNote?.It),
        ListingTextField.Access => Clean(document.CheckInMethod?.It),
        ListingTextField.Neighborhood => Join(document.Zone?.It, document.Near?.It),
        ListingTextField.Transport => Clean(document.Translations.GetValueOrDefault(ListingLanguages.Italian)?.Transport),
        ListingTextField.Rules => Join(property.HouseRules, document.RulesNote?.It),
        _ => null,
    };

    private static string? OtherText(SitePropertyProfileDocument document, string language, ListingTextField field)
    {
        var written = Clean(document.Translations.GetValueOrDefault(language)?.Get(field));
        if (written is not null)
            return written;

        return field switch
        {
            ListingTextField.Spaces => Join(Part(document.FloorNote, language), Part(document.OutdoorNote, language)),
            ListingTextField.Access => Clean(Part(document.CheckInMethod, language)),
            ListingTextField.Neighborhood => Join(Part(document.Zone, language), Part(document.Near, language)),
            _ => null,
        };
    }

    // The part of a profile text in a language other than Italian: the English one, or the one of Others.
    private static string? Part(LocalizedText? text, string language) =>
        text is null ? null : language == ListingLanguages.English ? text.En : text.Others?.GetValueOrDefault(language);

    private static string? Clean(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();

    private static string? Join(params string?[] parts)
    {
        var written = parts.Select(Clean).OfType<string>().ToList();
        return written.Count == 0 ? null : string.Join(Separator, written);
    }
}
