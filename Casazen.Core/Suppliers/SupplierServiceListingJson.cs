using System.Text.Json;
using System.Text.Json.Serialization;

namespace Casazen.Core.Suppliers;

/// <summary>
/// Reads and writes the JSON columns of <c>SupplierServiceListing</c> (<c>SupplementsJson</c>, <c>IncludedJson</c>,
/// <c>ExcludedJson</c>, <c>PhotoUrlsJson</c>): camelCase, no null members (<c>max</c> is left out when there is none).
/// </summary>
public static class SupplierServiceListingJson
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>The JSON array of <paramref name="items"/>.</summary>
    public static string Serialize<T>(IEnumerable<T> items) => JsonSerializer.Serialize(items, Options);

    /// <summary>The supplements stored in <paramref name="json"/>; an empty list for a missing or unreadable value.</summary>
    public static IReadOnlyList<SupplierServiceSupplement> ReadSupplements(string? json) =>
        Read<SupplierServiceSupplement>(json);

    /// <summary>The strings stored in <paramref name="json"/>; an empty list for a missing or unreadable value.</summary>
    public static IReadOnlyList<string> ReadStrings(string? json) => Read<string>(json);

    private static List<T> Read<T>(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return [];

        try
        {
            return JsonSerializer.Deserialize<List<T>>(json, Options)?.Where(item => item is not null).ToList() ?? [];
        }
        catch (JsonException)
        {
            // Written by this module only; an unreadable value is treated as empty rather than failing a whole catalog read.
            return [];
        }
    }
}
