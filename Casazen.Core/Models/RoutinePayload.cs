using System.Text.Json;

namespace Casazen.Core.Models;

/// <summary>
/// The payload of an event as the routine engine handles it (E01/F04/US01/T01): a dictionary whose names are read in any case (the producers of the
/// outbox spell them as they like: <c>bookingId</c>, <c>BookingId</c>), made from whatever <c>IRoutineService.TriggerAsync</c> is given, and kept as
/// JSON while a Wait is pending. Ids and codes only: no personal data is ever put in a payload (ADR-022-R21), so none is kept here.
/// </summary>
public static class RoutinePayload
{
    public static IReadOnlyDictionary<string, object?> Normalize(object? payload)
    {
        var members = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        switch (payload)
        {
            case null:
                break;
            case IEnumerable<KeyValuePair<string, object?>> pairs:
                foreach (var pair in pairs)
                    members[pair.Key] = pair.Value;
                break;
            case JsonElement element:
                AddMembers(members, element);
                break;
            default:
                AddMembers(members, JsonSerializer.SerializeToElement(payload, payload.GetType()));
                break;
        }

        return members;
    }

    public static string ToJson(IReadOnlyDictionary<string, object?> payload)
    {
        ArgumentNullException.ThrowIfNull(payload);

        return JsonSerializer.Serialize(payload);
    }

    public static IReadOnlyDictionary<string, object?> FromJson(string? json)
    {
        var members = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(json))
            return members;

        try
        {
            using var document = JsonDocument.Parse(json);
            AddMembers(members, document.RootElement);
        }
        catch (JsonException)
        {
            members.Clear();
        }

        return members;
    }

    public static bool TryGetId(IReadOnlyDictionary<string, object?> payload, string name, out Guid id)
    {
        ArgumentNullException.ThrowIfNull(payload);

        id = Guid.Empty;
        if (!payload.TryGetValue(name, out var raw))
            return false;

        var found = raw switch
        {
            Guid guid => guid,
            string text when Guid.TryParse(text, out var parsed) => parsed,
            JsonElement { ValueKind: JsonValueKind.String } element when Guid.TryParse(element.GetString(), out var parsed) => parsed,
            _ => Guid.Empty,
        };
        id = found;
        return found != Guid.Empty;
    }

    private static void AddMembers(Dictionary<string, object?> members, JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
            return;

        foreach (var member in element.EnumerateObject())
            members[member.Name] = member.Value.Clone();
    }
}
