using Casazen.Core.Entities;
using Casazen.Core.Exceptions;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Services;

/// <summary>
/// Photo gallery of a property (PC-04, A2-26) on <see cref="IFileStorage"/>: objects in the public bucket, absolute URLs
/// in <c>Property.PhotoUrls</c>, first photo = cover (what the public pages already read).
/// </summary>
/// <remarks>
/// <para>Every change takes a per-property advisory lock (<see cref="PostgresAdvisoryLocks.Scope.PropertyPhotos"/>) and
/// re-reads the row after taking it: two parallel uploads, or an upload and a deletion, never overwrite each other's
/// photo list. The row is read through the tenant query filter, so another org's property does not exist here.</para>
/// <para>An upload is validated entirely before anything is stored (all or none), the objects are stored outside the lock
/// (random names cannot clash), the list is extended under the lock, and the objects of a failed or over-limit upload are
/// removed again. A deletion removes the object from the bucket first and the entry afterwards, both under the lock: if the
/// storage fails the photo stays listed and the host can retry, instead of leaving an object nobody can reach and delete.</para>
/// </remarks>
public class PropertyPhotoService(
    AppDbContext db,
    IFileStorage storage,
    IImageStorageService images,
    ILogger<PropertyPhotoService> logger,
    TimeProvider? timeProvider = null) : IPropertyPhotoService
{
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;

    public async Task<IReadOnlyList<string>> AddAsync(
        Guid propertyId,
        IReadOnlyList<IFormFile> files,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(files);
        if (files.Count == 0)
            throw new DomainRuleException(PropertyPhotoLimits.NoFileCode, "PropertyPhotoNone");
        if (files.Count > PropertyPhotoLimits.MaxFilesPerRequest)
            throw new DomainRuleException(
                PropertyPhotoLimits.TooManyFilesCode, "PropertyPhotoTooManyFiles", PropertyPhotoLimits.MaxFilesPerRequest);

        // Nothing is stored unless every file is acceptable.
        foreach (var file in files)
            await ValidateAsync(file, cancellationToken);

        // Cheap early answer before uploading anything; the limit is checked again under the lock.
        var current = await db.Properties.AsNoTracking()
            .Where(p => p.Id == propertyId)
            .Select(p => p.PhotoUrls)
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw PropertyNotFound(propertyId);
        EnsureRoom(current.Count, files.Count);

        var uploaded = new List<string>(files.Count);
        try
        {
            foreach (var file in files)
                uploaded.Add(await images.UploadImageAsync(file, propertyId));

            var gallery = await ChangeGalleryAsync(
                propertyId,
                property =>
                {
                    EnsureRoom(property.PhotoUrls.Count, uploaded.Count);
                    return Task.FromResult<IReadOnlyList<string>>([.. property.PhotoUrls, .. uploaded]);
                },
                cancellationToken);
            logger.LogInformation(
                "Property {PropertyId}: {Count} photo(s) added, gallery now {Total}", propertyId, uploaded.Count, gallery.Count);
            return gallery;
        }
        catch
        {
            await RemoveObjectsAsync(propertyId, uploaded);
            throw;
        }
    }

    public async Task<IReadOnlyList<string>> DeleteAsync(
        Guid propertyId,
        string photoUrl,
        CancellationToken cancellationToken = default)
    {
        var gallery = await ChangeGalleryAsync(
            propertyId,
            async property =>
            {
                if (!property.PhotoUrls.Contains(photoUrl, StringComparer.Ordinal))
                    throw PhotoNotFound(propertyId);

                await DeleteObjectAsync(propertyId, photoUrl, cancellationToken);
                return [.. property.PhotoUrls.Where(url => !string.Equals(url, photoUrl, StringComparison.Ordinal))];
            },
            cancellationToken);
        logger.LogInformation("Property {PropertyId}: photo deleted, gallery now {Total}", propertyId, gallery.Count);
        return gallery;
    }

    public async Task<IReadOnlyList<string>> ReorderAsync(
        Guid propertyId,
        IReadOnlyList<string> orderedUrls,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(orderedUrls);
        var gallery = await ChangeGalleryAsync(
            propertyId,
            property =>
            {
                // The same photos, each once: a list from a stale page (a photo added or deleted elsewhere) is refused
                // instead of silently dropping or resurrecting photos.
                var expected = property.PhotoUrls.Order(StringComparer.Ordinal);
                var received = orderedUrls.Order(StringComparer.Ordinal);
                if (orderedUrls.Count != property.PhotoUrls.Count || !expected.SequenceEqual(received, StringComparer.Ordinal))
                    throw new DomainConflictException(PropertyPhotoLimits.GalleryChangedCode, "PropertyPhotosChanged");

                return Task.FromResult<IReadOnlyList<string>>([.. orderedUrls]);
            },
            cancellationToken);
        logger.LogInformation("Property {PropertyId}: gallery reordered ({Total} photos)", propertyId, gallery.Count);
        return gallery;
    }

    public async Task<IReadOnlyList<string>> SetCoverAsync(
        Guid propertyId,
        string photoUrl,
        CancellationToken cancellationToken = default)
    {
        var gallery = await ChangeGalleryAsync(
            propertyId,
            property =>
            {
                if (!property.PhotoUrls.Contains(photoUrl, StringComparer.Ordinal))
                    throw PhotoNotFound(propertyId);

                // The cover first, the others in their current order.
                return Task.FromResult<IReadOnlyList<string>>(
                    [photoUrl, .. property.PhotoUrls.Where(url => !string.Equals(url, photoUrl, StringComparison.Ordinal))]);
            },
            cancellationToken);
        logger.LogInformation("Property {PropertyId}: cover photo set", propertyId);
        return gallery;
    }

    /// <summary>
    /// Runs <paramref name="change"/> on the current gallery of the property under the property's photo lock and saves
    /// the list it returns (only <c>PhotoUrls</c> and <c>UpdatedAt</c> are written). Nothing is saved when it throws.
    /// </summary>
    private async Task<IReadOnlyList<string>> ChangeGalleryAsync(
        Guid propertyId,
        Func<Property, Task<IReadOnlyList<string>>> change,
        CancellationToken cancellationToken)
    {
        await using var transaction = await PostgresAdvisoryLocks.BeginLockedTransactionAsync(
            db, cancellationToken, (PostgresAdvisoryLocks.Scope.PropertyPhotos, propertyId.ToString()));

        var property = await db.Properties.FirstOrDefaultAsync(p => p.Id == propertyId, cancellationToken)
            ?? throw PropertyNotFound(propertyId);
        // The request may already have read (and tracked) this row before the lock was free: the photo list must be the
        // committed one of the previous holder, not the copy of the first read.
        await db.Entry(property).ReloadAsync(cancellationToken);

        var photos = await change(property);
        property.PhotoUrls = [.. photos];
        property.UpdatedAt = _clock.GetUtcNow().UtcDateTime;
        await db.SaveChangesAsync(cancellationToken);

        if (transaction is not null)
            await transaction.CommitAsync(cancellationToken);

        return property.PhotoUrls;
    }

    /// <summary>
    /// 422 for a file that is empty, too large, not JPEG/PNG/WebP by extension or declared type, or whose content is not
    /// the image it claims to be.
    /// </summary>
    private async Task ValidateAsync(IFormFile file, CancellationToken cancellationToken)
    {
        var name = DisplayName(file);
        if (file is null || file.Length == 0 || file.Length > PropertyPhotoLimits.MaxFileSizeBytes)
            throw new DomainRuleException(
                PropertyPhotoLimits.InvalidSizeCode, "PropertyPhotoInvalidSize", name, PropertyPhotoLimits.MaxFileSizeBytes / (1024 * 1024));

        if (!images.ValidateImage(file))
            throw InvalidType(name);

        // The declared type and the extension are the client's word: the content decides.
        await using var content = file.OpenReadStream();
        var detected = await ImageSignature.DetectAsync(content, cancellationToken);
        if (detected is null
            || !string.Equals(detected, file.ContentType, StringComparison.OrdinalIgnoreCase)
            || !ExtensionMatches(Path.GetExtension(file.FileName), detected))
        {
            throw InvalidType(name);
        }
    }

    private static bool ExtensionMatches(string extension, string contentType) =>
        (extension.ToLowerInvariant(), contentType) switch
        {
            (".jpg" or ".jpeg", "image/jpeg") => true,
            (".png", "image/png") => true,
            (".webp", "image/webp") => true,
            _ => false,
        };

    private static DomainRuleException InvalidType(string fileName) =>
        new(PropertyPhotoLimits.InvalidTypeCode, "PropertyPhotoInvalidType", fileName);

    private static string DisplayName(IFormFile? file)
    {
        var name = Path.GetFileName(file?.FileName ?? string.Empty);
        return name.Length > 100 ? name[..100] : name;
    }

    private static void EnsureRoom(int currentCount, int adding)
    {
        if (currentCount + adding > PropertyPhotoLimits.MaxPhotos)
            throw new DomainRuleException(
                PropertyPhotoLimits.LimitReachedCode, "PropertyPhotoLimitReached", PropertyPhotoLimits.MaxPhotos, currentCount);
    }

    /// <summary>
    /// Deletes the object of a photo of this property from the public bucket. An entry that is not an object of this
    /// property's own folder (legacy relative path, external URL, another property's photo) is left alone: a property's
    /// gallery can never delete what belongs to somebody else.
    /// </summary>
    private async Task DeleteObjectAsync(Guid propertyId, string photoUrl, CancellationToken cancellationToken)
    {
        var key = storage.TryGetPublicKey(photoUrl);
        if (key is not null && key.StartsWith(StorageKeys.PropertyPhoto(propertyId, string.Empty), StringComparison.Ordinal))
        {
            await storage.DeleteAsync(StorageBucket.Public, key, cancellationToken);
            return;
        }

        logger.LogWarning(
            "Property {PropertyId}: photo entry is not an object of the property's own storage folder, removed from the list only",
            propertyId);
    }

    /// <summary>Best effort removal of objects whose upload did not end in the gallery; never hides the original error.</summary>
    private async Task RemoveObjectsAsync(Guid propertyId, IReadOnlyList<string> urls)
    {
        foreach (var url in urls)
        {
            try
            {
                await DeleteObjectAsync(propertyId, url, CancellationToken.None);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Property {PropertyId}: could not remove the object of a photo that was not added", propertyId);
            }
        }
    }

    private static NotFoundException PropertyNotFound(Guid propertyId) =>
        new($"Property {propertyId} not found");

    private static NotFoundException PhotoNotFound(Guid propertyId) =>
        new($"Photo not in the gallery of property {propertyId}")
        {
            Code = PropertyPhotoLimits.NotFoundCode,
            MessageKey = "PropertyPhotoNotFound",
        };
}
