using System.Text.Json;
using System.Text.Json.Nodes;

namespace Casazen.Core.Services;

public static class ListingContentGroups
{
    public const string Property = "property";

    public const string Descriptions = "descriptions";

    public const string Rooms = "rooms";

    public static IReadOnlyList<string> All { get; } = [Property, Descriptions, Rooms];
}

/// <summary>
/// What a channel row keeps in <c>ListingChannelState.ContentHashesJson</c> (E02/F03/US01/T02, T07, ADR-027-R1, ADR-034): the SHA-256 of each group
/// of the content as the channel manager returned it right after the last accepted push (the baseline that tells a change made outside CasaZen),
/// and, in members that start with an underscore, the publication process the row waits on and the groups found changed. One reader and one writer,
/// so no caller handles the JSON by hand. No personal data: hashes, ids and group names.
/// </summary>
/// <param name="Hashes">The hash of each group, by group name (<see cref="ListingContentGroups"/>); empty = no push accepted yet.</param>
/// <param name="ProcessId">The publication process of the last push (<c>GET .../process/{id}</c>); <c>null</c> = none to follow.</param>
/// <param name="ChangedGroups">The groups found different from the baseline; empty = none.</param>
public sealed record ListingChannelBaseline(
    IReadOnlyDictionary<string, string> Hashes,
    string? ProcessId,
    IReadOnlyList<string> ChangedGroups)
{
    private const string ProcessKey = "_processId";

    private const string ChangedKey = "_changed";

    public static ListingChannelBaseline Empty { get; } = new(new Dictionary<string, string>(), null, []);

    public bool HasBaseline => Hashes.Count > 0;

    public static ListingChannelBaseline Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return Empty;

        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                return Empty;

            var hashes = new Dictionary<string, string>(StringComparer.Ordinal);
            string? process = null;
            var changed = new List<string>();
            foreach (var property in document.RootElement.EnumerateObject())
            {
                switch (property.Name)
                {
                    case ProcessKey:
                        process = property.Value.ValueKind == JsonValueKind.String ? property.Value.GetString() : null;
                        break;
                    case ChangedKey:
                        if (property.Value.ValueKind == JsonValueKind.Array)
                            changed.AddRange(property.Value.EnumerateArray().Where(v => v.ValueKind == JsonValueKind.String).Select(v => v.GetString()!));
                        break;
                    default:
                        if (!property.Name.StartsWith('_') && property.Value.ValueKind == JsonValueKind.String && property.Value.GetString() is { Length: > 0 } hash)
                            hashes[property.Name] = hash;
                        break;
                }
            }

            return new ListingChannelBaseline(hashes, string.IsNullOrWhiteSpace(process) ? null : process, changed);
        }
        catch (JsonException)
        {
            return Empty;
        }
    }

    public string? ToJson()
    {
        if (Hashes.Count == 0 && ProcessId is null && ChangedGroups.Count == 0)
            return null;

        var root = new JsonObject();
        foreach (var (group, hash) in Hashes.OrderBy(entry => entry.Key, StringComparer.Ordinal))
            root[group] = hash;

        if (ProcessId is not null)
            root[ProcessKey] = ProcessId;

        if (ChangedGroups.Count > 0)
        {
            var changed = new JsonArray();
            foreach (var group in ChangedGroups.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
                changed.Add(group);

            root[ChangedKey] = changed;
        }

        return root.ToJsonString();
    }

    public ListingChannelBaseline WithProcess(string? processId) => this with { ProcessId = processId };

    public ListingChannelBaseline WithChanged(IReadOnlyList<string> groups) => this with { ChangedGroups = groups };

    public IReadOnlyList<string> Differences(IReadOnlyDictionary<string, string> current)
    {
        ArgumentNullException.ThrowIfNull(current);

        return
        [
            .. Hashes
                .Where(entry => current.TryGetValue(entry.Key, out var hash) && !string.Equals(hash, entry.Value, StringComparison.Ordinal))
                .Select(entry => entry.Key)
                .Order(StringComparer.Ordinal),
        ];
    }
}
