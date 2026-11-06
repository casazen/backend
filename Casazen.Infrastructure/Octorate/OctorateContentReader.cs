using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Casazen.Core.Services;

namespace Casazen.Infrastructure.Octorate;

/// <summary>
/// Reads the groups of the content of a listing back from the channel manager and gives the SHA-256 of each (E02/F03/US01/T02, T07, ADR-027-R1):
/// the baseline stored after an accepted push, and the current state the changes made outside CasaZen are found against. It only reads: nothing
/// here writes to Octorate. The hash is of a canonical form (sorted members, no spacing), so two reads of the same content give the same hash
/// whatever order Octorate answers in.
/// </summary>
/// <remarks>
/// The groups are the ones Octorate answers a read for: the accommodation (<see cref="ListingContentGroups.Property"/>), the descriptions and the
/// house rules, and the rooms with their beds. The rules of the stay, the prices and the discounts can be written but no read returns them (sandbox,
/// 2026-11-04), so they have no baseline and no change made on them outside CasaZen is ever detected. The caller names the org first
/// (<see cref="IOctorateOrgScope.Use"/>): the client takes none.
/// </remarks>
public sealed class OctorateContentReader(IOctorateApiClient client)
{
    /// <summary>The hash of each group of the content of the unit <paramref name="productId"/> of the accommodation.</summary>
    public async Task<IReadOnlyDictionary<string, string>> ReadAsync(string accommodationId, string productId, CancellationToken ct)
    {
        var property = await client.GetPropertyAsync(accommodationId, ct);
        var descriptions = await client.GetDescriptionsAsync(accommodationId, productId, ct);
        var rooms = await client.GetContentRoomsAsync(accommodationId, productId, ct);

        return new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            [ListingContentGroups.Property] = HashOf(PropertyForm(property)),
            [ListingContentGroups.Descriptions] = HashOf(DescriptionsForm(descriptions)),
            [ListingContentGroups.Rooms] = HashOf(RoomsForm(rooms)),
        };
    }

    /// <summary>The SHA-256 of a canonical text, in lower-case hexadecimal.</summary>
    internal static string HashOf(string canonical) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();

    internal static string PropertyForm(OctorateProperty property)
    {
        ArgumentNullException.ThrowIfNull(property);

        return new JsonObject
        {
            ["name"] = property.Name?.Trim(),
            ["address"] = property.Address?.Trim(),
            ["city"] = property.City?.Trim(),
            ["zipCode"] = property.PostalCode?.Trim(),
            ["latitude"] = property.Latitude?.ToString("0.#####", CultureInfo.InvariantCulture),
            ["longitude"] = property.Longitude?.ToString("0.#####", CultureInfo.InvariantCulture),
        }.ToJsonString();
    }

    internal static string DescriptionsForm(OctorateDescriptions? descriptions)
    {
        var form = new JsonObject
        {
            ["description"] = Map(descriptions?.Description),
            ["houseRules"] = Map(descriptions?.HouseRules),
            ["directions"] = descriptions?.Directions?.Trim(),
        };
        return form.ToJsonString();
    }

    internal static string RoomsForm(IReadOnlyList<OctorateContentRoom> rooms)
    {
        var list = new JsonArray();
        foreach (var room in rooms.OrderBy(r => r.RoomNumber).ThenBy(r => r.Type, StringComparer.Ordinal))
        {
            var beds = new JsonArray();
            foreach (var bed in room.Beds.OrderBy(b => b.Type, StringComparer.Ordinal))
                beds.Add(new JsonObject { ["type"] = bed.Type, ["quantity"] = bed.Quantity });

            list.Add(new JsonObject { ["type"] = room.Type, ["number"] = room.RoomNumber, ["maxGuests"] = room.MaxGuests, ["beds"] = beds });
        }

        return list.ToJsonString();
    }

    private static JsonObject Map(IReadOnlyDictionary<string, string>? texts)
    {
        var map = new JsonObject();
        if (texts is null)
            return map;

        foreach (var (language, text) in texts.OrderBy(entry => entry.Key, StringComparer.Ordinal))
            map[language] = text.Trim();

        return map;
    }
}
