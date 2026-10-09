using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Services;
using Casazen.Core.Suppliers;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Services;

// The photos of the work (SP-04): the supplier adds them while the work is on, the host and the supplier read them. They live in
// the PRIVATE bucket of IFileStorage under service-requests/{requestId}/photos/ and are read only through an authenticated
// endpoint (the controller authorizes the request, this service opens the file of that request).
public partial class ServiceRequestService
{
    public async Task<ServiceRequest> AddWorkPhotosAsync(
        Guid id,
        Guid supplierOrgId,
        IReadOnlyList<IFormFile> photos,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(photos);
        if (photos.Count == 0)
            throw new ArgumentException("At least one photo is needed.", nameof(photos));

        var request = await GetRequestForActiveSupplierOrThrow(id, supplierOrgId, cancellationToken);

        // The photos are the evidence of the work: added while it is on, before it is completed.
        if (request.Status is not (ServiceRequestStatus.PresoInCarico or ServiceRequestStatus.InCorso))
        {
            throw new DomainRuleException(
                ServiceRequestErrorCodes.InvalidTransition, ServiceRequestErrorCodes.CannotAddPhotosMessageKey);
        }

        var current = ServiceRequestJson.ReadPhotos(request.WorkPhotosJson).ToList();
        if (current.Count + photos.Count > ServiceRequestLimits.MaxWorkPhotos)
        {
            throw new DomainRuleException(
                ServiceRequestErrorCodes.PhotoLimitReached,
                ServiceRequestErrorCodes.PhotoLimitReachedMessageKey,
                ServiceRequestLimits.MaxWorkPhotos,
                current.Count,
                photos.Count);
        }

        // All or none: every file is checked on its content before any of them is stored.
        var contentTypes = new List<string>(photos.Count);
        foreach (var photo in photos)
            contentTypes.Add(await ValidatePhotoAsync(photo, cancellationToken));

        var stored = new List<ServiceRequestPhoto>(photos.Count);
        try
        {
            for (var index = 0; index < photos.Count; index++)
            {
                var key = StorageKeys.ServiceRequestPhoto(request.Id, StorageKeys.NewFileName(photos[index].FileName));
                await using var content = photos[index].OpenReadStream();
                await fileStorage.PutAsync(StorageBucket.Private, key, content, contentTypes[index], cancellationToken);
                stored.Add(new ServiceRequestPhoto(Guid.NewGuid(), key, Now()));
            }

            // Saved under the xmin check: two uploads at once never overwrite each other's list (the second gets 409 and retries).
            request.WorkPhotosJson = ServiceRequestJson.Serialize(current.Concat(stored));
            request.UpdatedAt = Now();
            await SaveAsync(request, "work photos", cancellationToken);
        }
        catch
        {
            await RemoveObjectsAsync(stored);
            throw;
        }

        logger.LogInformation("ServiceRequest {Id}: {Count} work photo(s) added", request.Id, stored.Count);
        return request;
    }

    public async Task<ServiceRequestPhotoContent?> OpenWorkPhotoAsync(
        ServiceRequest request,
        Guid photoId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var photos = ServiceRequestJson.ReadPhotos(request.WorkPhotosJson);
        var position = photos.ToList().FindIndex(photo => photo.Id == photoId);
        if (position < 0)
            return null;

        // Only an object of this request's own folder of the private bucket is ever read.
        var key = photos[position].Key;
        if (!StorageKeys.IsValid(key) || !key.StartsWith($"service-requests/{request.Id}/photos/", StringComparison.Ordinal))
        {
            logger.LogWarning("ServiceRequest {Id}: work photo {PhotoId} has a storage key outside its folder", request.Id, photoId);
            return null;
        }

        var content = await fileStorage.OpenReadAsync(StorageBucket.Private, key, cancellationToken);
        if (content is null)
        {
            logger.LogWarning("ServiceRequest {Id}: the file of work photo {PhotoId} is missing from the storage", request.Id, photoId);
            return null;
        }

        return new ServiceRequestPhotoContent(content, StorageKeys.ContentTypeFor(key), $"lavoro-{position + 1}{Path.GetExtension(key)}");
    }

    /// <summary>
    /// 422 <see cref="ServiceRequestErrorCodes.PhotoInvalid"/> for a file that is empty, too large, not JPEG/PNG/WebP by
    /// extension or declared type, or whose content is not the image it claims to be (the declared type and the extension are
    /// the client's word: the content decides). Returns the content type found in the file.
    /// </summary>
    private async Task<string> ValidatePhotoAsync(IFormFile file, CancellationToken cancellationToken)
    {
        var name = DisplayName(file);
        if (file is null || file.Length == 0 || file.Length > ServiceRequestLimits.MaxPhotoFileSizeBytes || !images.ValidateImage(file))
            throw PhotoInvalid(name);

        await using var content = file.OpenReadStream();
        var detected = await ImageSignature.DetectAsync(content, cancellationToken);
        if (detected is null
            || !string.Equals(detected, file.ContentType, StringComparison.OrdinalIgnoreCase)
            || !ExtensionMatches(Path.GetExtension(file.FileName), detected))
        {
            throw PhotoInvalid(name);
        }

        return detected;
    }

    private static bool ExtensionMatches(string extension, string contentType) =>
        (extension.ToLowerInvariant(), contentType) switch
        {
            (".jpg" or ".jpeg", "image/jpeg") => true,
            (".png", "image/png") => true,
            (".webp", "image/webp") => true,
            _ => false,
        };

    private static DomainRuleException PhotoInvalid(string fileName) =>
        new(ServiceRequestErrorCodes.PhotoInvalid, ServiceRequestErrorCodes.PhotoInvalidMessageKey, fileName);

    private static string DisplayName(IFormFile? file)
    {
        var name = Path.GetFileName(file?.FileName ?? string.Empty);
        return name.Length > 100 ? name[..100] : name;
    }

    /// <summary>Removes objects stored by an upload that failed, so none is left that no request lists. Best effort.</summary>
    private async Task RemoveObjectsAsync(IReadOnlyList<ServiceRequestPhoto> stored)
    {
        foreach (var photo in stored)
        {
            try
            {
                await fileStorage.DeleteAsync(StorageBucket.Private, photo.Key);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Could not remove the work photo object {Key} of a failed upload", photo.Key);
            }
        }
    }
}
