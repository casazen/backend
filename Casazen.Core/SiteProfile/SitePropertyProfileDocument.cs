using System.Text.Json;
using System.Text.Json.Serialization;

namespace Casazen.Core.SiteProfile;

/// <summary>
/// A text the host writes for the page in Italian (always) and, if they want, in English. The page shows the language of the visitor
/// and falls back to Italian when there is no English text: nothing is translated automatically (DB-D8).
/// </summary>
/// <param name="It">The Italian text, never blank.</param>
/// <param name="En">The English text; <c>null</c> = the host wrote none.</param>
public sealed record LocalizedText(string It, string? En = null);

/// <summary>One of the (at most three) highlights of the page: an icon of the fixed list, a short title and an optional line.</summary>
/// <param name="Icon">One of <see cref="SitePropertyProfileLimits.HighlightIcons"/>.</param>
/// <param name="Title">The headline, e.g. «Piscina privata».</param>
/// <param name="Text">A line that completes it; <c>null</c> = none.</param>
public sealed record SiteHighlight(string Icon, LocalizedText Title, LocalizedText? Text = null);

/// <summary>
/// What the host wrote for the property page, stored in <c>Properties.SiteProfileJson</c> (DB-11a). Camel-case JSON; a member that
/// is absent is simply not written, a member this release does not know is ignored on reading, so a later release can add members
/// without a migration. <see cref="V"/> is the version of the format.
/// </summary>
public sealed record SitePropertyProfileDocument
{
    /// <summary>Version of the format; a change that older code cannot read gets a new number.</summary>
    public const int CurrentVersion = 1;

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

    /// <summary>Nothing is written.</summary>
    [JsonIgnore]
    public bool IsEmpty =>
        Zone is null && Near is null && FloorNote is null && OutdoorNote is null && CheckInMethod is null && RulesNote is null
        && Highlights.Count == 0 && SafetyNotes.Count == 0 && Tags.Count == 0;
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

    // A text without its Italian part is not a text (a hand-edited column, a bug of an older release): left out rather than shown half.
    private static SitePropertyProfileDocument Clean(SitePropertyProfileDocument? document)
    {
        if (document is null)
            return Empty;

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
        };
    }

    private static LocalizedText? CleanText(LocalizedText? text) =>
        text is null || string.IsNullOrWhiteSpace(text.It)
            ? null
            : string.IsNullOrWhiteSpace(text.En) ? text with { En = null } : text;

    /// <summary>The text to store: <c>null</c> for a profile that holds nothing, so the column stays empty.</summary>
    public static string? Serialize(SitePropertyProfileDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        return document.IsEmpty ? null : JsonSerializer.Serialize(document, Options);
    }
}
