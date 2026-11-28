namespace Casazen.Core.Regulatory;

/// <summary>
/// The closed lists of the three statistical fields of a stay guest (NF-40, ADR-030-R1): <c>StayGuest.TourismType</c>,
/// <c>StayGuest.TravelWay</c> and <c>StayGuest.EducationLevel</c>, as CasaZen's own neutral codes. The codes of each region arrive with a
/// verified regional format (ADR-016-R7) and are never guessed from these; until then nothing is sent anywhere from these fields. Whether a
/// field is required is decided by the readiness rules of the check-in, never at entry: here a value is only checked against its list.
/// </summary>
public static class IstatGuestFieldLists
{
    public const int MaxCodeLength = 30;

    public const string TourismTypeField = "TourismType";

    public const string TravelWayField = "TravelWay";

    public const string EducationLevelField = "EducationLevel";

    public static readonly IReadOnlyList<string> TourismTypes = ["holiday", "business", "other"];

    public static readonly IReadOnlyList<string> TravelWays = ["car", "train", "plane", "bus", "ship", "other"];

    public static readonly IReadOnlyList<string> EducationLevels =
        ["none", "primary", "lower_secondary", "upper_secondary", "degree", "postgraduate"];

    public static readonly IReadOnlyDictionary<string, IReadOnlyList<string>> ByField = new Dictionary<string, IReadOnlyList<string>>
    {
        [TourismTypeField] = TourismTypes,
        [TravelWayField] = TravelWays,
        [EducationLevelField] = EducationLevels,
    };

    public static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim().ToLowerInvariant();

    public static bool IsValid(string field, string? normalizedValue) =>
        normalizedValue is null
        || (ByField.TryGetValue(field, out var list) && list.Contains(normalizedValue, StringComparer.Ordinal));
}
