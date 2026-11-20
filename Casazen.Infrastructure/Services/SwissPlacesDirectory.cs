using Casazen.Core.Entities;
using Casazen.Core.Exceptions;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Casazen.Infrastructure.Services;

/// <inheritdoc cref="ISwissPlacesDirectory" />
public class SwissPlacesDirectory(AppDbContext db) : ISwissPlacesDirectory
{
    public async Task<IReadOnlyList<SwissLocalityMatch>> SearchLocalitiesAsync(string? query, int limit, CancellationToken cancellationToken = default)
    {
        var text = query?.Trim() ?? string.Empty;
        if (text.Length == 0 || text.Length > ISwissPlacesDirectory.MaxQueryLength)
            return [];

        var take = Math.Clamp(limit, 1, ISwissPlacesDirectory.MaxLimit);
        var places = from locality in db.SwissLocalities.AsNoTracking()
                     join municipality in db.SwissMunicipalities.AsNoTracking() on locality.MunicipalityCode equals municipality.MunicipalityCode
                     select new { locality, municipality };

        // A postal code, or its first digits: «69» lists the postal codes of the Lugano area, «6900» Lugano, Massagno and Paradiso.
        var digits = PostalCodePrefix(text);
        if (digits is not null)
        {
            var byPostalCode = await places
                .Where(p => p.locality.PostalCode.StartsWith(digits))
                .OrderBy(p => p.locality.PostalCode == digits ? 0 : 1)
                .ThenBy(p => p.locality.PostalCode)
                .ThenBy(p => p.locality.AddOn)
                .ThenBy(p => p.locality.Name)
                .ThenBy(p => p.municipality.OfficialName)
                .Take(take * 2)
                .Select(p => new SwissLocalityMatch(p.locality.PostalCode, p.locality.Name, p.locality.MunicipalityCode, p.municipality.OfficialName, p.municipality.CantonCode))
                .ToListAsync(cancellationToken);
            return await ResultAsync([.. byPostalCode.Distinct().Take(take)], cancellationToken);
        }

        if (text.All(char.IsAsciiDigit))
            return [];

        // A name: the exact name first, then the names that start with the query, then the ones that contain it.
        var name = SwissLocality.NormalizeName(text);
        if (name.Length < ISwissPlacesDirectory.MinNameLength)
            return [];

        var byName = await places
            .Where(p => p.locality.NormalizedName.Contains(name))
            .OrderBy(p => p.locality.NormalizedName == name ? 0 : p.locality.NormalizedName.StartsWith(name) ? 1 : 2)
            .ThenBy(p => p.locality.Name)
            .ThenBy(p => p.locality.PostalCode)
            .ThenBy(p => p.locality.AddOn)
            .ThenBy(p => p.municipality.OfficialName)
            .Take(take * 2)
            .Select(p => new SwissLocalityMatch(p.locality.PostalCode, p.locality.Name, p.locality.MunicipalityCode, p.municipality.OfficialName, p.municipality.CantonCode))
            .ToListAsync(cancellationToken);
        return await ResultAsync([.. byName.Distinct().Take(take)], cancellationToken);
    }

    public async Task<SwissMunicipalityInfo?> FindMunicipalityAsync(string? municipalityCode, CancellationToken cancellationToken = default)
    {
        if (SwissPlaceCodes.NormalizeMunicipalityCode(municipalityCode) is not { } code)
            return null;

        var municipality = await db.SwissMunicipalities.AsNoTracking()
            .Where(m => m.MunicipalityCode == code)
            .Select(m => new SwissMunicipalityInfo(m.MunicipalityCode, m.OfficialName, m.CantonCode))
            .FirstOrDefaultAsync(cancellationToken);

        // A number «unknown» to a directory that is not loaded is not unknown: the directory cannot answer.
        if (municipality is null && !await db.SwissMunicipalities.AsNoTracking().AnyAsync(cancellationToken))
            throw Unavailable();

        return municipality;
    }

    /// <summary>
    /// The answer of a search that reached the directory. An empty one is a search that found nothing only when the directory is loaded: with no
    /// municipality or no locality at all (the load at startup failed or was turned off, a new database) it is 422 <c>swiss_places_unavailable</c>,
    /// as the save of a municipality answers, never an empty list that looks like «no such place».
    /// </summary>
    private async Task<IReadOnlyList<SwissLocalityMatch>> ResultAsync(List<SwissLocalityMatch> matches, CancellationToken cancellationToken)
    {
        if (matches.Count == 0
            && !(await db.SwissMunicipalities.AsNoTracking().AnyAsync(cancellationToken) && await db.SwissLocalities.AsNoTracking().AnyAsync(cancellationToken)))
        {
            throw Unavailable();
        }

        return matches;
    }

    private static DomainRuleException Unavailable() => new(SwissPlaceErrorCodes.Unavailable, SwissPlaceErrorCodes.UnavailableMessageKey);

    /// <summary>One to four digits, after an optional <c>CH-</c> prefix and spaces: the start of a postal code; <c>null</c> for anything else.</summary>
    private static string? PostalCodePrefix(string text)
    {
        var compact = string.Concat(text.Where(c => !char.IsWhiteSpace(c))).ToUpperInvariant();
        if (compact.StartsWith("CH-", StringComparison.Ordinal))
            compact = compact[3..];
        else if (compact.StartsWith("CH", StringComparison.Ordinal) && compact.Length > 2 && char.IsAsciiDigit(compact[2]))
            compact = compact[2..];

        return compact.Length is >= 1 and <= 4 && compact.All(char.IsAsciiDigit) ? compact : null;
    }
}
