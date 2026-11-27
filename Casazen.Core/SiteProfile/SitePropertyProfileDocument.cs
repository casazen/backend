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

/// <summary>One of the (at most three) highlights of the page: an icon of the fixed list, a short title and an optional line.</summary>
/// <param name="Icon">One of <see cref="SitePropertyProfileLimits.HighlightIcons"/>.</param>
/// <param name="Title">The headline, e.g. «Piscina privata».</param>
/// <param name="Text">A line that completes it; <c>null</c> = none.</param>
public sealed record SiteHighlight(string Icon, LocalizedText Title, LocalizedText? Text = null);

/// <summary>
/// The texts of a listing in one language other than Italian (NF-31, ADR-035): the seven fields of the channel manager, plain strings. A field
/// that is <c>null</c> was not written in that language. Italian is not kept here (it is in the columns of the property and in the
/// <see cref="LocalizedText.It"/> part of the profile texts), but for <see cref="Transport"/>, which has no other place.
/// </summary>
public sealed record ListingTexts(
    string? Title = null,
    string? Description = null,
    string? Spaces = null,
    string? Access = null,
    string? Neighborhood = null,
    string? Transport = null,
    string? Rules = null)
{
    /// <summary>Nothing is written in any field.</summary>
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

    /// <summary>The same texts with <paramref name="field"/> set to <paramref name="value"/> (<c>null</c> clears it).</summary>
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
    /// <summary>Version of the format; a change that older code cannot read gets a new number.</summary>
    public const int CurrentVersion = 1;

    private static readonly IReadOnlyDictionary<string, ListingTexts> NoTranslations = new Dictionary<string, ListingTexts>(StringComparer.Ordinal);

    private static readonly IReadOnlyDictionary<string, TextOrigin> NoOrigins = new Dictionary<string, TextOrigin>(StringComparer.Ordinal);

    public int V { get; init; } = CurrentVersion;

    /// <summary>The zone of the house: «Campagna di Ostuni, Valle d'Itria».</summary>
    public LocalizedText? Zone { get; init; }

    /// <summary>What is near: «10 minuti da Ostuni, 20 dal mare».</summary>
    public LocalizedText? Near { get; init; }

    /// <summary>A note about the floor: «Piano terra, senza scale».</summary>
    public LocalizedText? FloorNote { get; init; }

    /// <summary>A note about the outdoor spaces: «2.000 m² di giardino con uliveto».</summary>
    public LocalizedText? OutdoorNote { get; init; }

    /// <summary>How check-in works: «In autonomia, con cassetta a codice».</summary>
    public LocalizedText? CheckInMethod { get; init; }

    /// <summary>A note the host adds to the house rules.</summary>
    public LocalizedText? RulesNote { get; init; }

    /// <summary>Up to <see cref="SitePropertyProfileLimits.MaxHighlights"/> highlights.</summary>
    public IReadOnlyList<SiteHighlight> Highlights { get; init => field = value ?? []; } = [];

    /// <summary>Up to <see cref="SitePropertyProfileLimits.MaxSafetyNotes"/> notes: «La piscina non è sorvegliata».</summary>
    public IReadOnlyList<LocalizedText> SafetyNotes { get; init => field = value ?? []; } = [];

    /// <summary>The labels the host chose, ids of <see cref="SitePropertyProfileLimits.Tags"/>.</summary>
    public IReadOnlyList<string> Tags { get; init => field = value ?? []; } = [];

    /// <summary>
    /// The texts of the listing in the languages of the channel manager (<see cref="ListingLanguages.All"/>): language code to
    /// <see cref="ListingTexts"/>. Italian is in the columns of the property and in the profile texts, so it appears here only for the
    /// transport; English is here like every other language. Never copied to the columns.
    /// </summary>
    public IReadOnlyDictionary<string, ListingTexts> Translations { get; init => field = value ?? NoTranslations; } = NoTranslations;

    /// <summary>
    /// Who wrote each text of <see cref="Translations"/> (<see cref="TextOrigin"/>): key <c>"&lt;language&gt;.&lt;field&gt;"</c> (<c>fr.title</c>,
    /// <c>en.neighborhood</c>). A text with no entry was written by a person.
    /// </summary>
    public IReadOnlyDictionary<string, TextOrigin> Origins { get; init => field = value ?? NoOrigins; } = NoOrigins;

    /// <summary>Nothing is written.</summary>
    [JsonIgnore]
    public bool IsEmpty =>
        Zone is null && Near is null && FloorNote is null && OutdoorNote is null && CheckInMethod is null && RulesNote is null
        && Highlights.Count == 0 && SafetyNotes.Count == 0 && Tags.Count == 0 && Translations.Count == 0;

    /// <summary>
    /// The document as the public pages read it: the texts in Italian and English only (the booking site reads nothing else), with no
    /// translation and no origin. Every public reader goes through here, so a language added for the channels never reaches a page.
    /// </summary>
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

/// <summary>Reads and writes the <c>Properties.SiteProfileJson</c> column.</summary>
public static class SitePropertyProfileJson
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>The profile that holds nothing: what a property that never had one has.</summary>
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

    /// <summary>The profile stored in a column, as a public page reads it (<see cref="SitePropertyProfileDocument.ForPublicPage"/>).</summary>
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

    // Only the languages of the listing other than Italian and English, each with a text.
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

    // Italian is kept only for the transport (its other texts are in the columns); a language without a text is left out.
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

    // An origin of a text that does not exist is left out, and so is one that is not a member of the enum.
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

    /// <summary>The text to store: <c>null</c> for a profile that holds nothing, so the column stays empty.</summary>
    public static string? Serialize(SitePropertyProfileDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        return document.IsEmpty ? null : JsonSerializer.Serialize(document, Options);
    }
}
