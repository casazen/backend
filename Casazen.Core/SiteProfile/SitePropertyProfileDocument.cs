using System.Text.Json;
using System.Text.Json.Serialization;
using Casazen.Core.Entities.Enums;

namespace Casazen.Core.SiteProfile;

/// <summary>
/// A text the host writes for the page in Italian (always) and, if they want, in English. The page shows the language of the visitor
/// and falls back to Italian when there is no English text: nothing is translated automatically (DB-D8).
/// </summary>
/// <param name="It">The Italian text, never blank.</param>
/// <param name="En">The English text; <c>null</c> = the host wrote none.</param>
public sealed record LocalizedText(string It, string? En = null)
{
    /// <summary>
    /// The same text in the other languages of the listing (NF-31, <see cref="ListingLanguages.Others"/>): language code to text. Only the
    /// profile texts of the property (zone, near, floor note, outdoor note, check-in method, note on the rules) carry it, for the channels
    /// that receive them: the public page reads <see cref="It"/> and <see cref="En"/> only and is never given this member
    /// (<see cref="SitePropertyProfileDocument.ForPublicPage"/>). <c>null</c> = none.
    /// </summary>
    public IReadOnlyDictionary<string, string>? Others { get; init; }
}

public sealed record SiteHighlight(string Icon, LocalizedText Title, LocalizedText? Text = null);

public sealed record ListingTexts(
    string? Title = null,
    string? Description = null,
    string? Spaces = null,
    string? Access = null,
    string? Neighborhood = null,
    string? Transport = null,
    string? Rules = null)
{
    [JsonIgnore]
    public bool IsEmpty => ListingTextResolver.Fields.All(candidate => string.IsNullOrWhiteSpace(Get(candidate)));

    public string? Get(ListingTextField field) => field switch
    {
        ListingTextField.Title => Title,
        ListingTextField.Description => Description,
        ListingTextField.Spaces => Spaces,
        ListingTextField.Access => Access,
        ListingTextField.Neighborhood => Neighborhood,
        ListingTextField.Transport => Transport,
        ListingTextField.Rules => Rules,
        _ => null,
    };

    public ListingTexts With(ListingTextField field, string? value) => field switch
    {
        ListingTextField.Title => this with { Title = value },
        ListingTextField.Description => this with { Description = value },
        ListingTextField.Spaces => this with { Spaces = value },
        ListingTextField.Access => this with { Access = value },
        ListingTextField.Neighborhood => this with { Neighborhood = value },
        ListingTextField.Transport => this with { Transport = value },
        ListingTextField.Rules => this with { Rules = value },
        _ => this,
    };
}

/// <summary>
/// What the host wrote for the property page, stored in <c>Properties.SiteProfileJson</c> (DB-11a). Camel-case JSON; a member that
/// is absent is simply not written, a member this release does not know is ignored on reading, so a later release can add members
/// without a migration. <see cref="V"/> is the version of the format.
/// </summary>
/// <remarks>
/// Since NF-31 the document is also the one place of the texts of the listing in the languages of the channel manager
/// (<see cref="Translations"/>, <see cref="Origins"/>, <see cref="LocalizedText.Others"/>); <see cref="ListingTextResolver"/> is the one
/// reader. Both members are optional: a document written before them reads as one with no translation.
/// </remarks>
public sealed record SitePropertyProfileDocument
{
    public const int CurrentVersion = 1;

    private static readonly IReadOnlyDictionary<string, ListingTexts> NoTranslations = new Dictionary<string, ListingTexts>(StringComparer.Ordinal);

    private static readonly IReadOnlyDictionary<string, TextOrigin> NoOrigins = new Dictionary<string, TextOrigin>(StringComparer.Ordinal);

    public int V { get; init; } = CurrentVersion;

    public LocalizedText? Zone { get; init; }

    public LocalizedText? Near { get; init; }

    public LocalizedText? FloorNote { get; init; }

    public LocalizedText? OutdoorNote { get; init; }

    public LocalizedText? CheckInMethod { get; init; }

    public LocalizedText? RulesNote { get; init; }

    public IReadOnlyList<SiteHighlight> Highlights { get; init => field = value ?? []; } = [];

    public IReadOnlyList<LocalizedText> SafetyNotes { get; init => field = value ?? []; } = [];

    public IReadOnlyList<string> Tags { get; init => field = value ?? []; } = [];

    /// <summary>
    /// The texts of the listing in the languages of the channel manager (<see cref="ListingLanguages.All"/>): language code to
    /// <see cref="ListingTexts"/>. Italian is in the columns of the property and in the profile texts, so it appears here only for the
    /// transport; English is here like every other language. Never copied to the columns.
    /// </summary>
    public IReadOnlyDictionary<string, ListingTexts> Translations { get; init => field = value ?? NoTranslations; } = NoTranslations;

    public IReadOnlyDictionary<string, TextOrigin> Origins { get; init => field = value ?? NoOrigins; } = NoOrigins;

    [JsonIgnore]
    public bool IsEmpty =>
        Zone is null && Near is null && FloorNote is null && OutdoorNote is null && CheckInMethod is null && RulesNote is null
        && Highlights.Count == 0 && SafetyNotes.Count == 0 && Tags.Count == 0 && Translations.Count == 0;

    public SitePropertyProfileDocument ForPublicPage() => this with
    {
        Zone = WithoutOthers(Zone),
        Near = WithoutOthers(Near),
        FloorNote = WithoutOthers(FloorNote),
        OutdoorNote = WithoutOthers(OutdoorNote),
        CheckInMethod = WithoutOthers(CheckInMethod),
        RulesNote = WithoutOthers(RulesNote),
        Highlights = [.. Highlights.Select(h => h with { Title = h.Title with { Others = null }, Text = WithoutOthers(h.Text) })],
        SafetyNotes = [.. SafetyNotes.Select(note => note with { Others = null })],
        Translations = NoTranslations,
        Origins = NoOrigins,
    };

    private static LocalizedText? WithoutOthers(LocalizedText? text) => text?.Others is null ? text : text with { Others = null };
}

public static class SitePropertyProfileJson
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static SitePropertyProfileDocument Empty { get; } = new();

    /// <summary>
    /// The profile stored in a column. A column that is empty, or holds something that cannot be read, is an empty profile: a public
    /// page must never fail because of a text a host saved.
    /// </summary>
    public static SitePropertyProfileDocument Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return Empty;

        try
        {
            return Clean(JsonSerializer.Deserialize<SitePropertyProfileDocument>(json, Options));
        }
        catch (JsonException)
        {
            return Empty;
        }
    }

    public static SitePropertyProfileDocument ParseForPublicPage(string? json) => Parse(json).ForPublicPage();

    // A text without its Italian part is not a text (a hand-edited column, a bug of an older release): left out rather than shown half.
    private static SitePropertyProfileDocument Clean(SitePropertyProfileDocument? document)
    {
        if (document is null)
            return Empty;

        var translations = CleanTranslations(document.Translations);
        return document with
        {
            Zone = CleanText(document.Zone),
            Near = CleanText(document.Near),
            FloorNote = CleanText(document.FloorNote),
            OutdoorNote = CleanText(document.OutdoorNote),
            CheckInMethod = CleanText(document.CheckInMethod),
            RulesNote = CleanText(document.RulesNote),
            Highlights =
            [
                .. document.Highlights
                    .Where(h => h is not null && !string.IsNullOrWhiteSpace(h.Icon) && CleanText(h.Title) is not null)
                    .Select(h => h with { Text = CleanText(h.Text) }),
            ],
            SafetyNotes = [.. document.SafetyNotes.Select(CleanText).OfType<LocalizedText>()],
            Tags = [.. document.Tags.Where(tag => !string.IsNullOrWhiteSpace(tag))],
            Translations = translations,
            Origins = CleanOrigins(document.Origins, translations),
        };
    }

    private static LocalizedText? CleanText(LocalizedText? text) =>
        text is null || string.IsNullOrWhiteSpace(text.It)
            ? null
            : text with { En = string.IsNullOrWhiteSpace(text.En) ? null : text.En, Others = CleanOthers(text.Others) };

    private static Dictionary<string, string>? CleanOthers(IReadOnlyDictionary<string, string>? others)
    {
        if (others is null)
            return null;

        var clean = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (code, value) in others)
        {
            var language = ListingLanguages.Normalize(code);
            if (language is null or ListingLanguages.Italian or ListingLanguages.English || string.IsNullOrWhiteSpace(value))
                continue;

            clean[language] = value;
        }

        return clean.Count == 0 ? null : clean;
    }

    private static SortedDictionary<string, ListingTexts> CleanTranslations(IReadOnlyDictionary<string, ListingTexts> translations)
    {
        var clean = new SortedDictionary<string, ListingTexts>(StringComparer.Ordinal);
        foreach (var (code, texts) in translations)
        {
            var language = ListingLanguages.Normalize(code);
            if (language is null || texts is null)
                continue;

            var kept = new ListingTexts();
            foreach (var field in ListingTextResolver.Fields)
            {
                if (language == ListingLanguages.Italian && field != ListingTextField.Transport)
                    continue;

                var text = texts.Get(field);
                if (!string.IsNullOrWhiteSpace(text))
                    kept = kept.With(field, text);
            }

            if (!kept.IsEmpty)
                clean[language] = kept;
        }

        return clean;
    }

    private static SortedDictionary<string, TextOrigin> CleanOrigins(
        IReadOnlyDictionary<string, TextOrigin> origins,
        IReadOnlyDictionary<string, ListingTexts> translations)
    {
        var clean = new SortedDictionary<string, TextOrigin>(StringComparer.Ordinal);
        foreach (var (key, origin) in origins)
        {
            var parts = key.Split('.', 2);
            var language = parts.Length == 2 ? ListingLanguages.Normalize(parts[0]) : null;
            if (language is null || !ListingTextResolver.TryParseField(parts[1], out var field) || !Enum.IsDefined(origin))
                continue;

            if (translations.TryGetValue(language, out var texts) && !string.IsNullOrWhiteSpace(texts.Get(field)))
                clean[ListingTextResolver.Key(language, field)] = origin;
        }

        return clean;
    }

    public static string? Serialize(SitePropertyProfileDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        return document.IsEmpty ? null : JsonSerializer.Serialize(document, Options);
    }
}
