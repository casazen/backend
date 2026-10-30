namespace Casazen.Core.SiteProfile;

/// <summary>
/// Limits and vocabularies of the site profile of a property (DB-11a): one place for the host form, the service, the columns and the
/// tests. A vocabulary here is part of the API contract: an id is never renamed (the stored profiles and the public pages use it).
/// </summary>
public static class SitePropertyProfileLimits
{
    /// <summary>Lowest floor a host can state: a basement.</summary>
    public const int MinFloorLevel = -3;

    /// <summary>Highest floor a host can state.</summary>
    public const int MaxFloorLevel = 60;

    /// <summary>Oldest renovation year a host can state.</summary>
    public const int MinRenovatedYear = 1800;

    /// <summary>The energy classes are at most two characters (<c>A4</c>); the column is <c>character varying(2)</c>.</summary>
    public const int EnergyClassMaxLength = 2;

    /// <summary>Largest energy index a host can state, in kWh/m² per year: above it is a typing mistake (the column is <c>numeric(7,2)</c>).</summary>
    public const decimal MaxEnergyIndex = 9999m;

    /// <summary>Decimals kept for the energy index.</summary>
    public const int EnergyIndexScale = 2;

    /// <summary>Longest address of a video or a tour (the columns are <c>character varying(300)</c>).</summary>
    public const int EmbedUrlMaxLength = 300;

    /// <summary>Highlights a property page can show.</summary>
    public const int MaxHighlights = 3;

    /// <summary>Safety notes a property page can show.</summary>
    public const int MaxSafetyNotes = 10;

    // Lengths of the texts, in characters, for the Italian and the English one alike.
    public const int ZoneMaxLength = 120;
    public const int NearMaxLength = 160;
    public const int FloorNoteMaxLength = 120;
    public const int OutdoorNoteMaxLength = 200;
    public const int CheckInMethodMaxLength = 200;
    public const int RulesNoteMaxLength = 500;
    public const int HighlightTitleMaxLength = 60;
    public const int HighlightTextMaxLength = 200;
    public const int SafetyNoteMaxLength = 160;

    /// <summary>Longest alternative text of a photo.</summary>
    public const int PhotoAltMaxLength = 200;

    /// <summary>
    /// The energy classes of an APE (D.Lgs. 192/2005), best first. The class is stated by the host from the APE it holds: the
    /// system never fills it in.
    /// </summary>
    public static IReadOnlyList<string> EnergyClasses { get; } = ["A4", "A3", "A2", "A1", "A", "B", "C", "D", "E", "F", "G"];

    /// <summary>
    /// The labels a house can carry on the booking site (the categories of the home page and the filter of the results), in the order
    /// they are shown. Some are decided by the data of the house (see <see cref="SitePropertyProfileRules.EffectiveTags"/>); the host
    /// can add any of them. <c>vista-mare</c> and the like have no other source than the host.
    /// </summary>
    public static IReadOnlyList<string> Tags { get; } = ["mare", "centro", "piscina", "pet", "campagna", "terrazza", "vista-mare"];

    /// <summary>
    /// The icons a highlight can use: the ones the booking site draws for its highlights (a fixed list, so a page never has to draw a
    /// name it does not know).
    /// </summary>
    public static IReadOnlyList<string> HighlightIcons { get; } =
    [
        "waves", "walk", "volume-x", "trees", "sunset", "sun", "landmark", "key", "door", "umbrella", "mountain", "panorama",
        "wine", "boat", "parking", "coffee", "flame", "wifi", "paw", "bed", "heart", "star", "home", "utensils", "snow",
        "sparkles", "shield", "lock", "tv", "droplet", "map-pin", "car", "train",
    ];
}
