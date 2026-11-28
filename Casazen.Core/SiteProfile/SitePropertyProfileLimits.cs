namespace Casazen.Core.SiteProfile;

public static class SitePropertyProfileLimits
{
    public const int MinFloorLevel = -3;

    public const int MaxFloorLevel = 60;

    public const int MinRenovatedYear = 1800;

    public const int EnergyClassMaxLength = 2;

    public const decimal MaxEnergyIndex = 9999m;

    public const int EnergyIndexScale = 2;

    public const int EmbedUrlMaxLength = 300;

    public const int MaxHighlights = 3;

    public const int MaxSafetyNotes = 10;

    public const int ZoneMaxLength = 120;
    public const int NearMaxLength = 160;
    public const int FloorNoteMaxLength = 120;
    public const int OutdoorNoteMaxLength = 200;
    public const int CheckInMethodMaxLength = 200;
    public const int RulesNoteMaxLength = 500;
    public const int HighlightTitleMaxLength = 60;
    public const int HighlightTextMaxLength = 200;
    public const int SafetyNoteMaxLength = 160;

    public const int PhotoAltMaxLength = 200;

    public const int ListingTextMaxLength = 1000;

    /// <summary>
    /// The energy classes of an APE (D.Lgs. 192/2005), best first. The class is stated by the host from the APE it holds: the
    /// system never fills it in.
    /// </summary>
    public static IReadOnlyList<string> EnergyClasses { get; } = ["A4", "A3", "A2", "A1", "A", "B", "C", "D", "E", "F", "G"];

    public static IReadOnlyList<string> Tags { get; } = ["mare", "centro", "piscina", "pet", "campagna", "terrazza", "vista-mare"];

    public static IReadOnlyList<string> HighlightIcons { get; } =
    [
        "waves", "walk", "volume-x", "trees", "sunset", "sun", "landmark", "key", "door", "umbrella", "mountain", "panorama",
        "wine", "boat", "parking", "coffee", "flame", "wifi", "paw", "bed", "heart", "star", "home", "utensils", "snow",
        "sparkles", "shield", "lock", "tv", "droplet", "map-pin", "car", "train",
    ];
}
