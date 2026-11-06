namespace Casazen.Core.Regulatory;

/// <summary>
/// The closed lists of the three statistical fields of a stay guest (NF-40, ADR-030-R1): <c>StayGuest.TourismType</c>,
/// <c>StayGuest.TravelWay</c> and <c>StayGuest.EducationLevel</c>, as CasaZen's own neutral codes. The codes of each region arrive with a
/// verified regional format (ADR-016-R7) and are never guessed from these; until then nothing is sent anywhere from these fields. Whether a
/// field is required is decided by the readiness rules of the check-in, never at entry: here a value is only checked against its list.
/// </summary>
public static class IstatGuestFieldLists
{
    /// <summary>Longest code of the three lists, and the length of their columns.</summary>
    public const int MaxCodeLength = 30;

    /// <summary>Name of the field of the type of tourism.</summary>
    public const string TourismTypeField = "TourismType";

    /// <summary>Name of the field of the way of travelling.</summary>
    public const string TravelWayField = "TravelWay";

    /// <summary>Name of the field of the education level.</summary>
    public const string EducationLevelField = "EducationLevel";

    /// <summary>Why the guest travels.</summary>
    public static readonly IReadOnlyList<string> TourismTypes = ["holiday", "business", "other"];

    /// <summary>How the guest arrived.</summary>
    public static readonly IReadOnlyList<string> TravelWays = ["car", "train", "plane", "bus", "ship", "other"];

    /// <summary>The highest education level of the guest.</summary>
    public static readonly IReadOnlyList<string> EducationLevels =
        ["none", "primary", "lower_secondary", "upper_secondary", "degree", "postgraduate"];

    /// <summary>The three statistical fields, by name, with their lists.</summary>
    public static readonly IReadOnlyDictionary<string, IReadOnlyList<string>> ByField = new Dictionary<string, IReadOnlyList<string>>
    {
        [TourismTypeField] = TourismTypes,
        [TravelWayField] = TravelWays,
        [EducationLevelField] = EducationLevels,
    };

    /// <summary>
    /// The value as stored: trimmed and lower case, or null when empty. A value outside the list is returned as it is, so the caller can
    /// refuse it with <see cref="IsValid"/>.
    /// </summary>
    public static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim().ToLowerInvariant();

    /// <summary>True for no value, or for a value (already normalized) of the list of <paramref name="field"/>.</summary>
    public static bool IsValid(string field, string? normalizedValue) =>
        normalizedValue is null
        || (ByField.TryGetValue(field, out var list) && list.Contains(normalizedValue, StringComparer.Ordinal));
}
