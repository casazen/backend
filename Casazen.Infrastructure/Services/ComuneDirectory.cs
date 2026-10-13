using Casazen.Core.Entities;
using Casazen.Core.Regulatory;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Casazen.Infrastructure.Services;

/// <summary>
/// Read side of the official ISTAT comuni list (SU-04), on the <c>Comuni</c> table. Platform reference data, the same for
/// every org (no tenant filter). Everything is read-only (<c>AsNoTracking</c>); the list is written only by
/// <see cref="ComuneImportService"/>.
/// </summary>
public class ComuneDirectory(AppDbContext db) : IComuneDirectory
{
    public async Task<ComuneDatasetStatus> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        var total = await db.Comuni.AsNoTracking().CountAsync(cancellationToken);
        var active = total == 0 ? 0 : await db.Comuni.AsNoTracking().CountAsync(c => c.IsActive, cancellationToken);

        var last = await db.ComuneImports
            .AsNoTracking()
            .OrderByDescending(i => i.ReferenceDate)
            .ThenByDescending(i => i.ImportedAt)
            .FirstOrDefaultAsync(cancellationToken);

        return new ComuneDatasetStatus(total, active, last is null ? null : ToInfo(last));
    }

    public Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default) =>
        db.Comuni.AsNoTracking().AnyAsync(c => c.IsActive, cancellationToken);

    public async Task<Comune?> FindByIstatCodeAsync(
        string? istatCode,
        bool activeOnly = true,
        CancellationToken cancellationToken = default)
    {
        var code = ComuneRules.NormalizeIstatCode(istatCode);
        if (code is null)
            return null;

        return await db.Comuni
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.IstatCode == code && (!activeOnly || c.IsActive), cancellationToken);
    }

    public async Task<IReadOnlyDictionary<string, Comune>> GetByIstatCodesAsync(
        IEnumerable<string> istatCodes,
        CancellationToken cancellationToken = default)
    {
        var codes = istatCodes
            .Select(ComuneRules.NormalizeIstatCode)
            .OfType<string>()
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (codes.Count == 0)
            return new Dictionary<string, Comune>(StringComparer.Ordinal);

        var rows = await db.Comuni.AsNoTracking().Where(c => codes.Contains(c.IstatCode)).ToListAsync(cancellationToken);
        return rows.ToDictionary(c => c.IstatCode, StringComparer.Ordinal);
    }

    public async Task<IReadOnlyList<Comune>> SearchAsync(string query, int limit, CancellationToken cancellationToken = default)
    {
        limit = Math.Clamp(limit, 1, 100);
        var trimmed = query?.Trim() ?? string.Empty;

        // An ISTAT code or a cadastral code finds its comune: a host who knows it types it.
        if (ComuneRules.NormalizeIstatCode(trimmed) is { } istat)
        {
            return await db.Comuni.AsNoTracking().Where(c => c.IsActive && c.IstatCode == istat).ToListAsync(cancellationToken);
        }

        if (ComuneRules.NormalizeCadastralCode(trimmed) is { } cadastral)
        {
            var byCode = await db.Comuni.AsNoTracking().Where(c => c.IsActive && c.CadastralCode == cadastral).ToListAsync(cancellationToken);
            if (byCode.Count > 0)
                return byCode;
        }

        var key = ComuneNames.Normalize(trimmed);
        if (key.Length == 0)
            return [];

        return await db.Comuni
            .AsNoTracking()
            .Where(c => c.IsActive && (c.NormalizedName.StartsWith(key) || c.SearchText.Contains(key)))
            .OrderBy(c => c.NormalizedName == key ? 0 : c.NormalizedName.StartsWith(key) ? 1 : 2)
            .ThenBy(c => c.NormalizedName.Length)
            .ThenBy(c => c.NormalizedName)
            .ThenBy(c => c.ProvinceCode)
            .Take(limit)
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyDictionary<string, Comune>> ResolveAsync(
        IEnumerable<string> identifiers,
        CancellationToken cancellationToken = default)
    {
        var tokens = identifiers
            .Select(t => t?.Trim() ?? string.Empty)
            .Where(t => t.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var result = new Dictionary<string, Comune>(StringComparer.OrdinalIgnoreCase);
        if (tokens.Count == 0)
            return result;

        var istatCodes = tokens.Where(ComuneRules.IsIstatCode).ToList();
        var cadastralCodes = tokens.Select(ComuneRules.NormalizeCadastralCode).OfType<string>().Distinct(StringComparer.Ordinal).ToList();
        var names = tokens.Select(ComuneNames.Normalize).Where(n => n.Length > 0).Distinct(StringComparer.Ordinal).ToList();

        var candidates = await db.Comuni
            .AsNoTracking()
            .Where(c => istatCodes.Contains(c.IstatCode)
                        || cadastralCodes.Contains(c.CadastralCode!)
                        || (c.IsActive && names.Contains(c.NormalizedName)))
            .ToListAsync(cancellationToken);

        foreach (var token in tokens)
        {
            // A code is exact (any status: an old code of a comune that no longer exists still says which one it was);
            // a name only when it is unique among the active comuni.
            if (ComuneRules.IsIstatCode(token))
            {
                var byIstat = candidates.FirstOrDefault(c => c.IstatCode == token);
                if (byIstat is not null)
                    result[token] = byIstat;
                continue;
            }

            if (ComuneRules.NormalizeCadastralCode(token) is { } cadastral)
            {
                var byCadastral = candidates
                    .Where(c => c.CadastralCode == cadastral)
                    .OrderByDescending(c => c.IsActive)
                    .FirstOrDefault();
                if (byCadastral is not null)
                {
                    result[token] = byCadastral;
                    continue;
                }
            }

            var normalized = ComuneNames.Normalize(token);
            var named = candidates.Where(c => c.IsActive && c.NormalizedName == normalized).Take(2).ToList();
            if (named.Count == 1)
                result[token] = named[0];
        }

        return result;
    }

    public async Task<Comune?> FindByNameAndProvinceAsync(
        string name,
        string provinceCode,
        CancellationToken cancellationToken = default)
    {
        var found = await FindByNamesAndProvincesAsync([(name, provinceCode)], cancellationToken);
        return found.FirstOrDefault();
    }

    public async Task<IReadOnlyList<Comune>> FindByNamesAndProvincesAsync(
        IEnumerable<(string Name, string ProvinceCode)> pairs,
        CancellationToken cancellationToken = default)
    {
        var wanted = pairs
            .Select(p => (Name: ComuneNames.Normalize(p.Name), Province: p.ProvinceCode.Trim().ToUpperInvariant()))
            .Where(p => p.Name.Length > 0 && ComuneRules.IsProvinceCode(p.Province))
            .Distinct()
            .ToList();
        if (wanted.Count == 0)
            return [];

        var names = wanted.Select(p => p.Name).Distinct().ToList();
        var provinces = wanted.Select(p => p.Province).Distinct().ToList();
        var candidates = await db.Comuni
            .AsNoTracking()
            .Where(c => c.IsActive && names.Contains(c.NormalizedName) && provinces.Contains(c.ProvinceCode))
            .ToListAsync(cancellationToken);

        return wanted
            .Select(p => candidates.FirstOrDefault(c => c.NormalizedName == p.Name && c.ProvinceCode == p.Province))
            .OfType<Comune>()
            .ToList();
    }

    private static ComuneImportInfo ToInfo(ComuneImport i) => new(
        i.Id, i.Origin, i.SourceFileName, i.SourceVersion, i.ReferenceDate, i.Sha256, i.RowCount, i.InsertedCount,
        i.UpdatedCount, i.UnchangedCount, i.DeactivatedCount, i.IsPartial, i.ImportedAt, i.ImportedBy);
}
