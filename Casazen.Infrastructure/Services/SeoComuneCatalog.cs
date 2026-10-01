using Casazen.Core.Regulatory;
using Casazen.Core.Services;

namespace Casazen.Infrastructure.Services;

/// <summary><see cref="ISeoComuneCatalog"/> on the official ISTAT list (<see cref="IComuneDirectory"/>).</summary>
public class SeoComuneCatalog(IComuneDirectory directory) : ISeoComuneCatalog
{
    public async Task<ComuneInfo?> GetByCodeAsync(string? istatCode, CancellationToken cancellationToken = default)
    {
        var comune = await directory.FindByIstatCodeAsync(istatCode, activeOnly: true, cancellationToken);
        return comune is null ? null : ComuneInfo.From(comune);
    }

    public async Task<IReadOnlyDictionary<string, ComuneInfo>> GetByCodesAsync(
        IEnumerable<string> istatCodes,
        CancellationToken cancellationToken = default)
    {
        var comuni = await directory.GetByIstatCodesAsync(istatCodes, cancellationToken);
        return comuni.Values.Where(c => c.IsActive).ToDictionary(c => c.IstatCode, ComuneInfo.From, StringComparer.Ordinal);
    }

    public async Task<ComuneInfo?> GetPilotBySlugAsync(string? comuneSlug, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(comuneSlug))
            return null;

        var slug = comuneSlug.Trim();
        return (await GetPilotsAsync(cancellationToken))
            .FirstOrDefault(c => c.ComuneSlug.Equals(slug, StringComparison.OrdinalIgnoreCase));
    }

    public async Task<ComuneInfo?> GetPilotByRegionAndComuneSlugAsync(
        string? regionSlug,
        string? comuneSlug,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(regionSlug) || string.IsNullOrWhiteSpace(comuneSlug))
            return null;

        return (await GetPilotsAsync(cancellationToken)).FirstOrDefault(c =>
            c.RegionSlug.Equals(regionSlug.Trim(), StringComparison.OrdinalIgnoreCase)
            && c.ComuneSlug.Equals(comuneSlug.Trim(), StringComparison.OrdinalIgnoreCase));
    }

    public async Task<IReadOnlyList<ComuneInfo>> GetPilotsAsync(CancellationToken cancellationToken = default)
    {
        var comuni = await directory.FindByNamesAndProvincesAsync(SeoPilotComuni.All, cancellationToken);
        return comuni.Select(ComuneInfo.From).ToList();
    }

    public async Task<ComuneInfo?> ResolveSlugOrCodeAsync(string? value, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var trimmed = value.Trim();
        return await GetPilotBySlugAsync(trimmed, cancellationToken) ?? await GetByCodeAsync(trimmed, cancellationToken);
    }
}
