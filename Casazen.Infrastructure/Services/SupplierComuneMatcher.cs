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

    public Task<IReadOnlyList<SupplierProfile>> FilterAsync(
        IReadOnlyCollection<SupplierProfile> suppliers,
        ComuneTarget target,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        return FilterAnyAsync(suppliers, [target], cancellationToken);
    }

    public async Task<IReadOnlyList<SupplierProfile>> FilterAnyAsync(
        IReadOnlyCollection<SupplierProfile> suppliers,
        IReadOnlyCollection<ComuneTarget> targets,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(suppliers);
        ArgumentNullException.ThrowIfNull(targets);

        var relevantTargets = targets.Where(target => !target.IsEmpty).ToList();
        if (relevantTargets.Count == 0 || suppliers.Count == 0)
            return [];

        var legacy = suppliers.ToDictionary(s => s.OrgId, s => ReadStrings(s.ComuniJson));

        // Resolve text identifiers for all locations and suppliers together.
        var identifiers = legacy.Values.SelectMany(values => values)
            .Concat(relevantTargets.Select(target => target.Name).OfType<string>());
        var resolved = await directory.ResolveAsync(identifiers, cancellationToken);
        var targetCodes = relevantTargets
            .Select(target => ComuneRules.NormalizeIstatCode(target.IstatCode)
                ?? (target.Name is { } text && resolved.TryGetValue(text.Trim(), out var comune) ? comune.IstatCode : null))
            .OfType<string>()
            .ToHashSet(StringComparer.Ordinal);
        var targetNames = relevantTargets.Select(target => ComuneNames.Normalize(target.Name))
            .Where(name => name.Length > 0)
            .ToHashSet(StringComparer.Ordinal);

        return suppliers.Where(supplier =>
        {
            var written = legacy[supplier.OrgId];
            var chosen = ReadStrings(supplier.ComuneIstatCodesJson);
            return chosen.Any(targetCodes.Contains) || written.Any(entry => Covers(entry, targetCodes, targetNames, resolved));
        }).ToList();
    }

    private static bool Covers(
        string entry,
        HashSet<string> targetCodes,
        HashSet<string> targetNames,
        IReadOnlyDictionary<string, Comune> resolved)
    {
        var trimmed = entry.Trim();
        var code = ComuneRules.IsIstatCode(trimmed) ? trimmed : resolved.GetValueOrDefault(trimmed)?.IstatCode;
        return (code is not null && targetCodes.Contains(code)) || targetNames.Contains(ComuneNames.Normalize(entry));
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
