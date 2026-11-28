using Casazen.Core.Entities;

namespace Casazen.Core.SiteProfile;

public enum ListingTextField
{
    Title = 0,
    Description = 1,

    Spaces = 2,

    Access = 3,

    Neighborhood = 4,

    Transport = 5,

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

    public static string Key(string language, ListingTextField field) => language + "." + CodeOf(field);

    public static string? Get(Property property, string language, ListingTextField field)
    {
        ArgumentNullException.ThrowIfNull(property);
        return Get(property, SitePropertyProfileJson.Parse(property.SiteProfileJson), language, field);
    }

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

    private static string? Part(LocalizedText? text, string language) =>
        text is null ? null : language == ListingLanguages.English ? text.En : text.Others?.GetValueOrDefault(language);

    private static string? Clean(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();

    private static string? Join(params string?[] parts)
    {
        var written = parts.Select(Clean).OfType<string>().ToList();
        return written.Count == 0 ? null : string.Join(Separator, written);
    }
}
