using Casazen.Core.DTOs;
using Casazen.Core.Exceptions;
using Casazen.Core.Services;
using Casazen.Core.SiteProfile;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Services;

/// <summary>
/// The alternative text of the photos of a property (DB-11a). Saving takes the per-property photo lock (the one of
/// <see cref="PropertyPhotoService"/>) and re-reads the row after taking it, so a photo uploaded or deleted at the same moment is neither lost
/// nor given a text that belongs to another. Only <c>PhotoAltJson</c> and <c>UpdatedAt</c> are written. The texts are never written to the log.
/// </summary>
public class PropertyPhotoAltService(
    AppDbContext db,
    ILogger<PropertyPhotoAltService> logger,
    TimeProvider? timeProvider = null) : IPropertyPhotoAltService
{
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;

    public async Task<IReadOnlyList<PublicPropertyPhotoDto>?> GetAsync(Guid propertyId, CancellationToken cancellationToken = default)
    {
        var row = await db.Properties.AsNoTracking()
            .Where(p => p.Id == propertyId)
            .Select(p => new { p.PhotoUrls, p.PhotoAltJson })
            .FirstOrDefaultAsync(cancellationToken);
        return row is null ? null : PropertyPhotoMeta.Of(row.PhotoUrls, row.PhotoAltJson);
    }

    public async Task<IReadOnlyList<PublicPropertyPhotoDto>?> SaveAsync(
        Guid propertyId,
        IReadOnlyList<PhotoAltInput> photos,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(photos);

        // The rules first: nothing is locked or read for a text that cannot be stored.
        var texts = new Dictionary<string, LocalizedText?>(StringComparer.Ordinal);
        foreach (var photo in photos)
        {
            texts[photo.Url] = SitePropertyProfileRules.NormalizeText(photo.Alt, SitePropertyProfileLimits.PhotoAltMaxLength, "photos");
        }

        await using var transaction = await PostgresAdvisoryLocks.BeginLockedTransactionAsync(
            db, cancellationToken, (PostgresAdvisoryLocks.Scope.PropertyPhotos, propertyId.ToString()));

        var property = await db.Properties.FirstOrDefaultAsync(p => p.Id == propertyId, cancellationToken);
        if (property is null)
            return null;

        // The request may already have read (and tracked) this row before the lock was free: the gallery must be the committed one.
        await db.Entry(property).ReloadAsync(cancellationToken);

        var unknown = texts.Keys.FirstOrDefault(url => !property.PhotoUrls.Contains(url, StringComparer.Ordinal));
        if (unknown is not null)
        {
            throw new NotFoundException($"Photo not in the gallery of property {propertyId}")
            {
                Code = PropertyPhotoLimits.NotFoundCode,
                MessageKey = "PropertyPhotoNotFound",
            };
        }

        // The texts of the photos listed; every other photo of the gallery loses its text; sizes stay as they are.
        var current = PropertyPhotoMeta.Parse(property.PhotoAltJson);
        var next = new Dictionary<string, PhotoMeta>(StringComparer.Ordinal);
        foreach (var url in property.PhotoUrls)
        {
            current.TryGetValue(url, out var known);
            var meta = new PhotoMeta(texts.GetValueOrDefault(url), known?.Width, known?.Height);
            if (!meta.IsEmpty)
                next[url] = meta;
        }

        property.PhotoAltJson = PropertyPhotoMeta.Serialize(next);
        property.UpdatedAt = _clock.GetUtcNow().UtcDateTime;
        await db.SaveChangesAsync(cancellationToken);

        if (transaction is not null)
            await transaction.CommitAsync(cancellationToken);

        logger.LogInformation("Photo texts of property {PropertyId} of org {OrgId} saved", property.Id, property.OrgId);
        return PropertyPhotoMeta.Of(property.PhotoUrls, property.PhotoAltJson);
    }
}
