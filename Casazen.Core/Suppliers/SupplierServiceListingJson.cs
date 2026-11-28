using System.Text.Json;
using System.Text.Json.Serialization;

namespace Casazen.Core.Suppliers;

public static class SupplierServiceListingJson
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static string Serialize<T>(IEnumerable<T> items) => JsonSerializer.Serialize(items, Options);

    public static IReadOnlyList<SupplierServiceSupplement> ReadSupplements(string? json) =>
        Read<SupplierServiceSupplement>(json);

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
