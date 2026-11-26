using System.Globalization;
using System.Text.Json;
using Casazen.Core.Entities;

namespace Casazen.Core.Models;

public static class MarketingSegmentRules
{
    public static readonly FieldDefinition[] Fields =
    [
        .. FieldCatalog.Fields.Where(f => f.FieldId is "totalStays" or "country" or "language" or "totalSpend" or "firstStayAt" or "lastStayAt" or "propertyHistory"),
        new("tag", ConditionFieldType.Set, "field.tag", ["in", "notIn"]),
        new("stage", ConditionFieldType.Enum, "field.stage", ["in", "notIn"]),
    ];

    public static string[] Strings(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try { return JsonSerializer.Deserialize<string[]>(json) ?? []; }
        catch (JsonException) { return []; }
    }

    public static IEnumerable<Guid> PropertyIds(GuestCrmProfile profile) =>
        Strings(profile.PropertyIdsJson).Select(value => Guid.TryParse(value, out var id) ? id : Guid.Empty).Where(id => id != Guid.Empty);

    public static bool Matches(ConditionRule rule, GuestCrmProfile profile, DateTime today, string stage)
    {
        if (!Fields.Any(field => field.FieldId == rule.FieldId && field.Operators.Contains(rule.Operator))) return false;
        var args = Arguments(rule.Value);
        if (args.Length == 0) return false;
        string[] values = rule.FieldId switch
        {
            "country" => [profile.Country],
            "language" => [profile.Language],
            "stage" => [stage],
            "tag" => Casazen.Core.Services.MarketingGuestCategories.EffectiveTags(profile).ToArray(),
            "propertyHistory" => Strings(profile.PropertyIdsJson),
            _ => [],
        };
        if (rule.Operator is "in" or "notIn")
        {
            bool present = values.Intersect(args, StringComparer.OrdinalIgnoreCase).Any();
            return rule.Operator == "in" ? present : !present;
        }
        if (rule.FieldId is "firstStayAt" or "lastStayAt")
        {
            DateTime date = rule.FieldId == "firstStayAt" ? profile.FirstStayAt : profile.LastStayAt;
            if (rule.Operator == "inLast") return int.TryParse(args[0], out var days) && days is >= 0 and <= 36500 && date >= today.AddDays(-days) && date <= today;
            if (!DateTimeOffset.TryParse(args[0], CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var start)) return false;
            return rule.Operator switch
            {
                "before" => date < start.UtcDateTime, "after" => date > start.UtcDateTime,
                "between" => args.Length == 2 && DateTimeOffset.TryParse(args[1], CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var end) && date >= start.UtcDateTime && date <= end.UtcDateTime,
                _ => false,
            };
        }
        decimal value = rule.FieldId == "totalStays" ? profile.TotalStays : profile.TotalSpend;
        if (!decimal.TryParse(args[0], NumberStyles.Number, CultureInfo.InvariantCulture, out var lower)) return false;
        return rule.Operator switch
        {
            "equals" => value == lower, "greaterThan" => value > lower, "lessThan" => value < lower,
            "between" => args.Length == 2 && decimal.TryParse(args[1], NumberStyles.Number, CultureInfo.InvariantCulture, out var upper) && value >= lower && value <= upper,
            _ => false,
        };
    }

    private static string[] Arguments(object? value)
    {
        if (value == null) return [];
        var json = JsonSerializer.SerializeToElement(value);
        return json.ValueKind == JsonValueKind.Array ? json.EnumerateArray().Select(v => v.ToString()).ToArray() : [json.ToString()];
    }
}

public sealed record MarketingSegmentFacts(GuestCrmProfile Profile, DateTime Today, string Stage);
