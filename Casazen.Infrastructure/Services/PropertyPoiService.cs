using Casazen.Core.DTOs;
using Casazen.Core.Entities;
using Casazen.Core.Exceptions;
using Casazen.Core.Services;
using Casazen.Core.SiteProfile;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Services;

/// <summary>
/// The points of interest around a property (DB-11a). The host sends the whole list; it replaces the stored one in one save, under the
/// property's media lock. Every figure is the host's own: the side of the compass is one of eight points and the distance is rounded and not
/// closer than <see cref="PropertyPoi.MinDistanceMeters"/> (<see cref="PoiDistance"/>) before it is stored, so what the page shows can never
/// be more exact than that, and nothing is worked out from the position of the property. The row is read through the tenant query filter.
/// Names are never written to the log.
/// </summary>
public class PropertyPoiService(
    AppDbContext db,
    ILogger<PropertyPoiService> logger,
    TimeProvider? timeProvider = null) : IPropertyPoiService
{
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;

    public async Task<IReadOnlyList<PublicPoiDto>?> ListAsync(Guid propertyId, CancellationToken cancellationToken = default)
    {
        if (!await db.Properties.AsNoTracking().AnyAsync(p => p.Id == propertyId, cancellationToken))
            return null;

        var rows = await db.PropertyPois.AsNoTracking()
            .Where(p => p.PropertyId == propertyId)
            .OrderBy(p => p.Position)
            .ToListAsync(cancellationToken);
        return [.. rows.Select(ToDto)];
    }

    public async Task<IReadOnlyList<PublicPoiDto>?> ReplaceAsync(
        Guid propertyId,
        IReadOnlyList<PoiInput> pois,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(pois);

        // The rules first: nothing is locked or read for a list that cannot be stored.
        if (pois.Count > PropertyPoi.MaxPerProperty)
            throw new DomainRuleException(PoiCodes.LimitReachedCode, "PropertyPoiLimitReached", PropertyPoi.MaxPerProperty);

        var normalized = new List<(PoiInput Input, LocalizedText Name)>(pois.Count);
        foreach (var poi in pois)
        {
            var name = SitePropertyProfileRules.NormalizeText(poi.Name, PropertyPoi.NameMaxLength, "pois")
                ?? throw new DomainRuleException(SitePropertyProfileErrorCodes.ItalianRequired, "PropertySiteProfileItalianRequired")
                {
                    Extensions = new Dictionary<string, object?> { ["field"] = "pois" },
                };
            if (poi.Minutes < PropertyPoi.MinMinutes || poi.Minutes > PropertyPoi.MaxMinutes)
            {
                throw new DomainRuleException(
                    PoiCodes.MinutesInvalidCode, "PropertyPoiMinutesInvalid", PropertyPoi.MinMinutes, PropertyPoi.MaxMinutes)
                {
                    Extensions = new Dictionary<string, object?> { ["field"] = "pois" },
                };
            }

            normalized.Add((poi, name));
        }

        await using var transaction = await PostgresAdvisoryLocks.BeginLockedTransactionAsync(
            db, cancellationToken, (PostgresAdvisoryLocks.Scope.PropertyPhotos, propertyId.ToString()));

        var property = await db.Properties.AsNoTracking()
            .Where(p => p.Id == propertyId)
            .Select(p => new { p.OrgId })
            .FirstOrDefaultAsync(cancellationToken);
        if (property is null)
            return null;

        var now = _clock.GetUtcNow().UtcDateTime;
        db.PropertyPois.RemoveRange(await db.PropertyPois.Where(p => p.PropertyId == propertyId).ToListAsync(cancellationToken));
        var rows = new List<PropertyPoi>(normalized.Count);
        for (var position = 0; position < normalized.Count; position++)
        {
            var (input, name) = normalized[position];
            rows.Add(new PropertyPoi
            {
                PropertyId = propertyId,
                OrgId = property.OrgId,
                Position = position,
                Kind = input.Kind,
                NameIt = name.It,
                NameEn = name.En,
                Mode = input.Mode,
                Minutes = input.Minutes,
                Direction = input.Direction,
                DistanceMeters = PoiDistance.Round(input.DistanceMeters),
                CreatedAt = now,
                UpdatedAt = now,
            });
        }

        db.PropertyPois.AddRange(rows);
        await db.SaveChangesAsync(cancellationToken);
        if (transaction is not null)
            await transaction.CommitAsync(cancellationToken);

        logger.LogInformation("Points of interest of property {PropertyId} saved: {Count}", propertyId, rows.Count);
        return [.. rows.Select(ToDto)];
    }

    private static PublicPoiDto ToDto(PropertyPoi row) =>
        new(row.Kind, new LocalizedText(row.NameIt, row.NameEn), row.Mode, row.Minutes, row.Direction, row.DistanceMeters);
}
