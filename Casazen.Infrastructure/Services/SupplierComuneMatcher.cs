using System.Text.Json;
using Casazen.Core.Entities;
using Casazen.Core.Regulatory;
using Casazen.Core.Services;
using Casazen.Core.Suppliers;

namespace Casazen.Infrastructure.Services;

/// <inheritdoc cref="ISupplierComuneMatcher" />
public class SupplierComuneMatcher(IComuneDirectory directory) : ISupplierComuneMatcher
{
    public async Task<bool> CoversAsync(SupplierProfile supplier, ComuneTarget target, CancellationToken cancellationToken = default) =>
        (await FilterAsync([supplier], target, cancellationToken)).Count == 1;

    public async Task<IReadOnlyList<SupplierProfile>> FilterAsync(
        IReadOnlyCollection<SupplierProfile> suppliers,
        ComuneTarget target,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(suppliers);
        ArgumentNullException.ThrowIfNull(target);

        if (target.IsEmpty || suppliers.Count == 0)
            return [];

        var legacy = suppliers.ToDictionary(s => s.OrgId, s => ReadStrings(s.ComuniJson));

        // One lookup for everything that is written as text: the target and every legacy entry of every supplier.
        var identifiers = legacy.Values.SelectMany(v => v).ToList();
        if (target.Name is { } name)
            identifiers.Add(name);
        var resolved = await directory.ResolveAsync(identifiers, cancellationToken);

        var targetIstat = ComuneRules.NormalizeIstatCode(target.IstatCode)
                          ?? (target.Name is { } targetText && resolved.TryGetValue(targetText.Trim(), out var comune) ? comune.IstatCode : null);
        var targetName = ComuneNames.Normalize(target.Name);

        var covering = new List<SupplierProfile>();
        foreach (var supplier in suppliers)
        {
            var written = legacy[supplier.OrgId];
            var chosen = ReadStrings(supplier.ComuneIstatCodesJson);

            if (targetIstat is not null && Covers(targetIstat, chosen, written, resolved))
                covering.Add(supplier);
            // Not resolvable (list not imported, unknown or ambiguous name): the old comparison of the written names.
            else if (targetName.Length > 0 && written.Any(w => ComuneNames.Normalize(w) == targetName))
                covering.Add(supplier);
        }

        return covering;
    }

    private static bool Covers(
        string istatCode,
        IReadOnlyList<string> chosen,
        IReadOnlyList<string> written,
        IReadOnlyDictionary<string, Comune> resolved)
    {
        if (chosen.Contains(istatCode, StringComparer.Ordinal))
            return true;

        foreach (var entry in written)
        {
            var trimmed = entry.Trim();
            if (ComuneRules.IsIstatCode(trimmed))
            {
                if (trimmed == istatCode)
                    return true;
            }
            else if (resolved.TryGetValue(trimmed, out var comune) && comune.IstatCode == istatCode)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>String items of a JSON array column; anything else (not an array, non-string items) is ignored.</summary>
    internal static IReadOnlyList<string> ReadStrings(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return [];

        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
                return [];

            return document.RootElement.EnumerateArray()
                .Where(item => item.ValueKind == JsonValueKind.String)
                .Select(item => item.GetString()!)
                .ToList();
        }
        catch (JsonException)
        {
            return [];
        }
    }
}
