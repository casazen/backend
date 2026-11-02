using Casazen.Core.DTOs;
using Casazen.Core.Entities;
using Casazen.Core.Exceptions;
using Casazen.Core.Services;
using Casazen.Core.SiteProfile;
using Casazen.Infrastructure.Data;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Services;

/// <summary>
/// The floor plan of a property (DB-11a) on <see cref="IFileStorage"/>: one PNG or JPEG per level in the property's own folder of the public
/// bucket, the absolute URL and the size in the row. An upload is validated entirely before anything is stored, the object is stored outside
/// the lock (a random name cannot clash), the row is added under the property's media lock (the one of the photos: <see cref="PostgresAdvisoryLocks.Scope.PropertyPhotos"/>)
/// with the limit checked again, and the object of a refused or failed upload is removed again. Deleting removes the object first and the row
/// afterwards, so a storage failure leaves the level listed for a retry. The row is read through the tenant query filter: another org's
/// property does not exist here. Names are never written to the log.
/// </summary>
public class PropertyFloorPlanService(
    AppDbContext db,
    IFileStorage storage,
    ILogger<PropertyFloorPlanService> logger,
    TimeProvider? timeProvider = null) : IPropertyFloorPlanService
{
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;

    public async Task<IReadOnlyList<PublicFloorPlanDto>?> ListAsync(Guid propertyId, CancellationToken cancellationToken = default)
    {
        if (!await db.Properties.AsNoTracking().AnyAsync(p => p.Id == propertyId, cancellationToken))
            return null;

        var rows = await db.PropertyFloorPlans.AsNoTracking()
            .Where(f => f.PropertyId == propertyId)
            .OrderBy(f => f.Level).ThenBy(f => f.Position).ThenBy(f => f.CreatedAt)
            .ToListAsync(cancellationToken);
        return [.. rows.Select(ToDto)];
    }

    public async Task<PublicFloorPlanDto?> AddAsync(Guid propertyId, IFormFile? image, FloorPlanInput input, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        // The rules first: nothing is read or stored for a level that cannot be saved.
        var (name, level, area) = Normalize(input);
        await ValidateImageAsync(image, cancellationToken);
        var file = image!;

        var property = await db.Properties.AsNoTracking()
            .Where(p => p.Id == propertyId)
            .Select(p => new { p.OrgId })
            .FirstOrDefaultAsync(cancellationToken);
        if (property is null)
            return null;

        // A cheap answer before uploading anything; the limit is checked again under the lock.
        if (await db.PropertyFloorPlans.CountAsync(f => f.PropertyId == propertyId, cancellationToken) >= PropertyFloorPlan.MaxPerProperty)
            throw LimitReached();

        var key = StorageKeys.PropertyFloorPlan(propertyId, StorageKeys.NewFileName(file.FileName));
        await using (var content = file.OpenReadStream())
        {
            await storage.PutAsync(StorageBucket.Public, key, content, file.ContentType, cancellationToken);
        }

        try
        {
            (int Width, int Height)? size;
            await using (var content = file.OpenReadStream())
            {
                size = await ImageSizeReader.ReadAsync(content, cancellationToken);
            }

            await using var transaction = await PostgresAdvisoryLocks.BeginLockedTransactionAsync(
                db, cancellationToken, (PostgresAdvisoryLocks.Scope.PropertyPhotos, propertyId.ToString()));

            var count = await db.PropertyFloorPlans.CountAsync(f => f.PropertyId == propertyId, cancellationToken);
            if (count >= PropertyFloorPlan.MaxPerProperty)
                throw LimitReached();

            var now = _clock.GetUtcNow().UtcDateTime;
            var row = new PropertyFloorPlan
            {
                PropertyId = propertyId,
                OrgId = property.OrgId,
                Level = level,
                Position = count,
                NameIt = name.It,
                NameEn = name.En,
                ImageUrl = storage.GetPublicUrl(key),
                ImageWidth = size?.Width,
                ImageHeight = size?.Height,
                AreaSqm = area,
                CreatedAt = now,
                UpdatedAt = now,
            };
            db.PropertyFloorPlans.Add(row);
            await db.SaveChangesAsync(cancellationToken);
            if (transaction is not null)
                await transaction.CommitAsync(cancellationToken);

            logger.LogInformation("Floor plan level added to property {PropertyId}", propertyId);
            return ToDto(row);
        }
        catch
        {
            await RemoveObjectAsync(key);
            throw;
        }
    }

    public async Task<PublicFloorPlanDto?> UpdateAsync(Guid propertyId, Guid planId, FloorPlanInput input, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        var (name, level, area) = Normalize(input);

        await using var transaction = await PostgresAdvisoryLocks.BeginLockedTransactionAsync(
            db, cancellationToken, (PostgresAdvisoryLocks.Scope.PropertyPhotos, propertyId.ToString()));

        if (!await db.Properties.AsNoTracking().AnyAsync(p => p.Id == propertyId, cancellationToken))
            return null;

        var row = await db.PropertyFloorPlans.FirstOrDefaultAsync(f => f.Id == planId && f.PropertyId == propertyId, cancellationToken)
            ?? throw NotFound(propertyId);
        row.Level = level;
        row.NameIt = name.It;
        row.NameEn = name.En;
        row.AreaSqm = area;
        row.UpdatedAt = _clock.GetUtcNow().UtcDateTime;
        await db.SaveChangesAsync(cancellationToken);
        if (transaction is not null)
            await transaction.CommitAsync(cancellationToken);

        return ToDto(row);
    }

    public async Task<bool> DeleteAsync(Guid propertyId, Guid planId, CancellationToken cancellationToken = default)
    {
        await using var transaction = await PostgresAdvisoryLocks.BeginLockedTransactionAsync(
            db, cancellationToken, (PostgresAdvisoryLocks.Scope.PropertyPhotos, propertyId.ToString()));

        if (!await db.Properties.AsNoTracking().AnyAsync(p => p.Id == propertyId, cancellationToken))
            return false;

        var row = await db.PropertyFloorPlans.FirstOrDefaultAsync(f => f.Id == planId && f.PropertyId == propertyId, cancellationToken)
            ?? throw NotFound(propertyId);

        // The object first: if the storage fails the level stays listed and the host can retry, instead of leaving an object nobody can reach.
        await RemoveObjectAsync(propertyId, row.ImageUrl, throwOnFailure: true, cancellationToken);
        db.PropertyFloorPlans.Remove(row);
        await db.SaveChangesAsync(cancellationToken);
        if (transaction is not null)
            await transaction.CommitAsync(cancellationToken);

        logger.LogInformation("Floor plan level removed from property {PropertyId}", propertyId);
        return true;
    }

    // ─── Rules ──────────────────────────────────────────────────────────────────────────────────────

    private static (LocalizedText Name, int Level, int? Area) Normalize(FloorPlanInput input)
    {
        var name = SitePropertyProfileRules.NormalizeText(input.Name, PropertyFloorPlan.NameMaxLength, "name")
            ?? throw Rule(SitePropertyProfileErrorCodes.ItalianRequired, "PropertySiteProfileItalianRequired", "name");

        var level = input.Level ?? 0;
        if (level < SitePropertyProfileLimits.MinFloorLevel || level > SitePropertyProfileLimits.MaxFloorLevel)
        {
            throw Rule(
                SitePropertyProfileErrorCodes.FloorLevelInvalid, "PropertySiteProfileFloorLevelInvalid", "level",
                SitePropertyProfileLimits.MinFloorLevel, SitePropertyProfileLimits.MaxFloorLevel);
        }

        if (input.AreaSqm is { } area && (area < PropertyFloorPlan.MinAreaSqm || area > PropertyFloorPlan.MaxAreaSqm))
        {
            throw Rule(
                FloorPlanLimits.AreaInvalidCode, "PropertyFloorPlanAreaInvalid", "areaSqm", PropertyFloorPlan.MinAreaSqm, PropertyFloorPlan.MaxAreaSqm);
        }

        return (name, level, input.AreaSqm);
    }

    /// <summary>422 for a file that is missing, empty, too large, not a PNG or JPEG by extension or declared type, or whose content is not the image it claims to be.</summary>
    private static async Task ValidateImageAsync(IFormFile? file, CancellationToken cancellationToken)
    {
        if (file is null)
            throw new DomainRuleException(FloorPlanLimits.NoFileCode, "PropertyFloorPlanNone");

        if (file.Length == 0 || file.Length > FloorPlanLimits.MaxFileSizeBytes)
        {
            throw new DomainRuleException(
                FloorPlanLimits.InvalidSizeCode, "PropertyFloorPlanInvalidSize", FloorPlanLimits.MaxFileSizeBytes / (1024 * 1024));
        }

        var extension = Path.GetExtension(file.FileName).ToLowerInvariant();
        var declared = file.ContentType.ToLowerInvariant();
        var extensionOk = (extension, declared) is (".png", "image/png") or (".jpg" or ".jpeg", "image/jpeg");
        if (!extensionOk)
            throw InvalidType();

        // The declared type and the extension are the client's word: the content decides.
        await using var content = file.OpenReadStream();
        var detected = await ImageSignature.DetectAsync(content, cancellationToken);
        if (!string.Equals(detected, declared, StringComparison.Ordinal))
            throw InvalidType();
    }

    // ─── Storage ────────────────────────────────────────────────────────────────────────────────────

    private async Task RemoveObjectAsync(string key)
    {
        try
        {
            await storage.DeleteAsync(StorageBucket.Public, key);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Could not remove the object of a floor plan that was not added");
        }
    }

    /// <summary>Removes the object of a level of this property's own folder; an address that is not one of the property's own objects is left alone.</summary>
    private async Task RemoveObjectAsync(Guid propertyId, string imageUrl, bool throwOnFailure, CancellationToken cancellationToken)
    {
        var key = storage.TryGetPublicKey(imageUrl);
        if (key is null || !key.StartsWith(StorageKeys.PropertyFloorPlan(propertyId, string.Empty), StringComparison.Ordinal))
        {
            logger.LogWarning("Floor plan of property {PropertyId} is not an object of its own folder, removed from the list only", propertyId);
            return;
        }

        try
        {
            await storage.DeleteAsync(StorageBucket.Public, key, cancellationToken);
        }
        catch (Exception ex) when (!throwOnFailure)
        {
            logger.LogError(ex, "Could not remove the object of a floor plan of property {PropertyId}", propertyId);
        }
    }

    // ─── Small helpers ──────────────────────────────────────────────────────────────────────────────

    private static PublicFloorPlanDto ToDto(PropertyFloorPlan row) =>
        new(row.Id, row.Level, new LocalizedText(row.NameIt, row.NameEn), row.ImageUrl, row.ImageWidth, row.ImageHeight, row.AreaSqm);

    private static DomainRuleException LimitReached() =>
        new(FloorPlanLimits.LimitReachedCode, "PropertyFloorPlanLimitReached", PropertyFloorPlan.MaxPerProperty);

    private static DomainRuleException InvalidType() =>
        new(FloorPlanLimits.InvalidTypeCode, "PropertyFloorPlanInvalidType");

    private static DomainRuleException Rule(string code, string messageKey, string field, params object[] args) =>
        new(code, messageKey, args) { Extensions = new Dictionary<string, object?> { ["field"] = field } };

    private static NotFoundException NotFound(Guid propertyId) =>
        new($"Floor plan level not on property {propertyId}")
        {
            Code = FloorPlanLimits.NotFoundCode,
            MessageKey = "PropertyFloorPlanNotFound",
        };
}
