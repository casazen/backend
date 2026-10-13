using Casazen.Core.Enums;

namespace Casazen.Core.Demo;

/// <summary>
/// The same document list the activation uses (<c>Compliance:RequiredDocuments</c>): the region's list, otherwise
/// <c>default</c>, otherwise the CIN certificate alone.
/// </summary>
public static class DemoRequiredDocuments
{
    public static IReadOnlyList<DocumentType> ForRegion(
        IReadOnlyDictionary<string, string[]> configured,
        string? regionCode)
    {
        string[]? names = null;
        if (!string.IsNullOrWhiteSpace(regionCode))
        {
            var key = configured.Keys.FirstOrDefault(k => k.Equals(regionCode.Trim(), StringComparison.OrdinalIgnoreCase));
            if (key is not null)
                configured.TryGetValue(key, out names);
        }

        if (names is not { Length: > 0 })
        {
            var fallback = configured.Keys.FirstOrDefault(k => k.Equals("default", StringComparison.OrdinalIgnoreCase));
            if (fallback is not null)
                configured.TryGetValue(fallback, out names);
        }

        var parsed = (names ?? ["CinCertificate"])
            .Select(name => Enum.TryParse<DocumentType>(name, ignoreCase: true, out var type) ? type : (DocumentType?)null)
            .Where(type => type is not null)
            .Select(type => type!.Value)
            .Distinct()
            .ToList();

        return parsed.Count > 0 ? parsed : [DocumentType.CinCertificate];
    }
}
