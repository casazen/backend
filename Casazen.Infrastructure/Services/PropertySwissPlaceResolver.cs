using Casazen.Core.Entities;
using Casazen.Core.Exceptions;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Casazen.Infrastructure.Services;

/// <inheritdoc cref="IPropertySwissPlaceResolver" />
public class PropertySwissPlaceResolver(AppDbContext db) : IPropertySwissPlaceResolver
{
    public async Task<SwissPlace?> ResolveAsync(Property property, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(property);
        if (!SwissPlace.IsSwissCountry(property.Country))
            return null;

        var places = await ResolveManyAsync([SwissPlaceLookup.From(property)], cancellationToken);
        return places.GetValueOrDefault(property.Id);
    }

    public async Task<IReadOnlyDictionary<Guid, SwissPlace>> ResolveManyAsync(
        IReadOnlyCollection<SwissPlaceLookup> properties,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(properties);
        var places = new Dictionary<Guid, SwissPlace>();
        var swiss = properties.Where(p => SwissPlace.IsSwissCountry(p.Country)).ToList();
        if (swiss.Count == 0)
            return places;

        // The municipality stored on the property, when the directory has it: the host confirmed it, it wins.
        var storedCodes = swiss
            .Select(p => SwissPlaceCodes.NormalizeMunicipalityCode(p.SwissMunicipalityCode))
            .OfType<string>()
            .Distinct(StringComparer.Ordinal)
            .ToList();
        var municipalities = storedCodes.Count == 0
            ? new Dictionary<string, SwissMunicipalityInfo>(StringComparer.Ordinal)
            : await db.SwissMunicipalities.AsNoTracking()
                .Where(m => storedCodes.Contains(m.MunicipalityCode))
                .Select(m => new SwissMunicipalityInfo(m.MunicipalityCode, m.OfficialName, m.CantonCode))
                .ToDictionaryAsync(m => m.MunicipalityCode, StringComparer.Ordinal, cancellationToken);

        var toDerive = new List<(SwissPlaceLookup Property, string PostalCode)>();
        foreach (var property in swiss)
        {
            if (SwissPlaceCodes.NormalizeMunicipalityCode(property.SwissMunicipalityCode) is { } code
                && municipalities.TryGetValue(code, out var stored))
            {
                places[property.PropertyId] = new SwissPlace(stored.MunicipalityCode, stored.Name, stored.CantonCode, SwissPlaceSource.Stored, Ambiguous: false);
            }
            else if (SwissPlaceCodes.FourDigitPostalCode(property.PostalCode) is { } postalCode)
            {
                toDerive.Add((property, postalCode));
            }
        }

        if (toDerive.Count == 0)
            return places;

        // Otherwise the localities of its postal code, with the municipality each lies in (one query for every property).
        var postalCodes = toDerive.Select(d => d.PostalCode).Distinct(StringComparer.Ordinal).ToList();
        var localities = await (
                from locality in db.SwissLocalities.AsNoTracking()
                join municipality in db.SwissMunicipalities.AsNoTracking() on locality.MunicipalityCode equals municipality.MunicipalityCode
                where postalCodes.Contains(locality.PostalCode)
                select new LocalityRow(locality.PostalCode, locality.NormalizedName, municipality.MunicipalityCode, municipality.OfficialName, municipality.CantonCode))
            .ToListAsync(cancellationToken);
        var byPostalCode = localities.ToLookup(l => l.PostalCode, StringComparer.Ordinal);

        foreach (var (property, postalCode) in toDerive)
        {
            var rows = byPostalCode[postalCode].ToList();
            if (rows.Count == 0)
                continue;

            places[property.PropertyId] = Derive(rows, property.City) is { } chosen
                ? new SwissPlace(chosen.MunicipalityCode, chosen.MunicipalityName, chosen.CantonCode, SwissPlaceSource.Derived, Ambiguous: false)
                : SwissPlace.AmbiguousPlace;
        }

        return places;
    }

    public async Task<SwissPlace> RequireMunicipalityAsync(string municipalityCode, CancellationToken cancellationToken = default)
    {
        if (SwissPlaceCodes.NormalizeMunicipalityCode(municipalityCode) is { } code
            && await db.SwissMunicipalities.AsNoTracking()
                .Where(m => m.MunicipalityCode == code)
                .Select(m => new SwissMunicipalityInfo(m.MunicipalityCode, m.OfficialName, m.CantonCode))
                .FirstOrDefaultAsync(cancellationToken) is { } municipality)
        {
            return new SwissPlace(municipality.MunicipalityCode, municipality.Name, municipality.CantonCode, SwissPlaceSource.Stored, Ambiguous: false);
        }

        if (!await db.SwissMunicipalities.AsNoTracking().AnyAsync(cancellationToken))
            throw new DomainRuleException(SwissPlaceErrorCodes.Unavailable, SwissPlaceErrorCodes.UnavailableMessageKey);

        throw new DomainRuleException(SwissPlaceErrorCodes.Unknown, SwissPlaceErrorCodes.UnknownMessageKey);
    }

    /// <summary>
    /// The municipality of the localities of one postal code: the only one; else the one whose official name is the city; else the one that has a
    /// locality of that postal code named as the city; else none (ambiguous). <c>6600</c> «Locarno» is Locarno, although the locality Locarno of
    /// 6600 lies in Muralto too: the name of the municipality decides before the one of the locality.
    /// </summary>
    private static LocalityRow? Derive(IReadOnlyList<LocalityRow> rows, string? city)
    {
        var candidates = rows.DistinctBy(r => r.MunicipalityCode).ToList();
        if (candidates.Count == 1)
            return candidates[0];

        var name = SwissLocality.NormalizeName(city);
        if (name.Length == 0)
            return null;

        var byMunicipality = candidates.Where(c => SwissLocality.NormalizeName(c.MunicipalityName) == name).ToList();
        if (byMunicipality.Count > 0)
            return byMunicipality.Count == 1 ? byMunicipality[0] : null;

        var byLocality = rows.Where(r => r.NormalizedName == name).DistinctBy(r => r.MunicipalityCode).ToList();
        return byLocality.Count == 1 ? byLocality[0] : null;
    }

    private sealed record LocalityRow(string PostalCode, string NormalizedName, string MunicipalityCode, string MunicipalityName, string CantonCode);
}
