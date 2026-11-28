using System.Text.Json;

namespace Casazen.Core.OrgTeam;

public static class OrgActivityRules
{
    public const int DefaultRetentionMonths = 12;

    public const int MaxRetentionMonths = 120;

    public const string RetentionMonthsConfigKey = "OrgTeam:ActivityRetentionMonths";

    public const int DefaultPageSize = 50;

    public const int MaxPageSize = 100;

    public const string SystemActor = "system";
}

public static class OrgActivityDetails
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = false };

    public const string NoAreas = "none";

    /// <summary>
    /// The code of a set of areas (rental contexts) as a detail value: the keys sorted and joined with a dot
    /// (<c>long-rent.short-rent</c>), which is a short code the log accepts. Not a sentence, never a name. No areas at all are
    /// <see cref="NoAreas"/>: an empty value is not a code and the log refuses it, so the change that repairs such a member could
    /// never be written.
    /// </summary>
    public static string AreasCode(IEnumerable<string> areas)
    {
        var code = string.Join('.', areas.Select(a => a.Trim().ToLowerInvariant()).Distinct().Order(StringComparer.Ordinal));
        return code.Length == 0 ? NoAreas : code;
    }

    public static string Serialize(IReadOnlyDictionary<string, string>? details)
    {
        if (details is null || details.Count == 0)
            return "{}";

        var ordered = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, value) in details)
            ordered[key] = value;

        return JsonSerializer.Serialize(ordered, Options);
    }

    public static IReadOnlyDictionary<string, string> Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return new Dictionary<string, string>();

        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, string>>(json, Options) ?? [];
        }
        catch (JsonException)
        {
            return new Dictionary<string, string>();
        }
    }
}
