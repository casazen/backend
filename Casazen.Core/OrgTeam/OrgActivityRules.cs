using System.Text.Json;

namespace Casazen.Core.OrgTeam;

/// <summary>The fixed numbers of the activity log (AM-02b), in one place so the services, the job, the endpoints and the tests read the same thing.</summary>
public static class OrgActivityRules
{
    /// <summary>
    /// Months a line is kept, counted back from now (decision D17: 12 months, <b>to be confirmed with the legal advisor</b>).
    /// Configurable as <c>OrgTeam:ActivityRetentionMonths</c>.
    /// </summary>
    public const int DefaultRetentionMonths = 12;

    /// <summary>The longest retention the configuration can ask for (ten years): a longer value is cut to this.</summary>
    public const int MaxRetentionMonths = 120;

    /// <summary>Configuration key of the retention.</summary>
    public const string RetentionMonthsConfigKey = "OrgTeam:ActivityRetentionMonths";

    /// <summary>Lines per page of the list when the client does not say.</summary>
    public const int DefaultPageSize = 50;

    public const int MaxPageSize = 100;

    /// <summary>The value of the <c>actor</c> filter that means «no person»: the lines written by a job or a webhook.</summary>
    public const string SystemActor = "system";
}

/// <summary>The details of an activity line as the database holds them (a JSON object of short codes) and as the code reads them.</summary>
public static class OrgActivityDetails
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = false };

    /// <summary>The code of a set of areas that holds none: a member with no rental membership (a drift the reconcile reports).</summary>
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

    /// <summary>The JSON of <paramref name="details"/>: keys in alphabetical order, so the same details always read the same; <c>{}</c> when there are none.</summary>
    public static string Serialize(IReadOnlyDictionary<string, string>? details)
    {
        if (details is null || details.Count == 0)
            return "{}";

        var ordered = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, value) in details)
            ordered[key] = value;

        return JsonSerializer.Serialize(ordered, Options);
    }

    /// <summary>
    /// The details a line holds. A stored value that is not the object <see cref="Serialize"/> writes reads as «no details»:
    /// the log is read by people, and one odd row must not stop the page.
    /// </summary>
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
