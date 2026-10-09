using System.Text.Json;
using Casazen.Core.Entities;

namespace Casazen.Web.DTOs.Supplier;

/// <summary>The comuni of a supplier profile as they are shown (SU-04): chosen from the official list, then written as text.</summary>
public static class SupplierComuniView
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    /// <summary>ISTAT codes of the comuni chosen from the official list.</summary>
    public static IReadOnlyList<string> IstatCodes(SupplierProfile profile) => ReadStrings(profile.ComuneIstatCodesJson);

    /// <summary>What the supplier wrote as text.</summary>
    public static IReadOnlyList<string> Written(SupplierProfile profile) => ReadStrings(profile.ComuniJson);

    /// <summary>
    /// Names of the comuni of the profile: the chosen ones by the name of the list (their ISTAT code when the list does not
    /// have the code), then what was written as text, without repeating a name.
    /// </summary>
    public static IReadOnlyList<string> Names(SupplierProfile profile, IReadOnlyDictionary<string, Comune>? listed)
    {
        var names = new List<string>();
        foreach (var code in IstatCodes(profile))
            names.Add(listed is not null && listed.TryGetValue(code, out var comune) ? comune.Name : code);

        foreach (var written in Written(profile))
        {
            if (!names.Contains(written, StringComparer.OrdinalIgnoreCase))
                names.Add(written);
        }

        return names;
    }

    private static IReadOnlyList<string> ReadStrings(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return [];

        try
        {
            return JsonSerializer.Deserialize<List<string>>(json, JsonOpts) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }
}
