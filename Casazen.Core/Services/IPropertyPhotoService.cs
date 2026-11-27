using Microsoft.AspNetCore.Http;

namespace Casazen.Core.Services;

public static class PropertyPhotoLimits
{
    public const int MaxPhotos = 20;

    public const int MaxFilesPerRequest = 10;

    public const long MaxFileSizeBytes = 10 * 1024 * 1024;

    public static readonly IReadOnlyList<string> AllowedContentTypes = ["image/jpeg", "image/png", "image/webp"];

    public const long MaxRequestBytes = MaxFilesPerRequest * MaxFileSizeBytes + 1024 * 1024;

    public const string NoFileCode = "property_photo_none";

    public const string TooManyFilesCode = "property_photo_too_many_files";

    public const string LimitReachedCode = "property_photo_limit_reached";

    public const string InvalidTypeCode = "property_photo_invalid_type";

    public const string InvalidSizeCode = "property_photo_invalid_size";

    public const string NotFoundCode = "property_photo_not_found";

    public const string GalleryChangedCode = "property_photos_changed";
}

/// <summary>
/// The photo gallery of a property: an ordered list of absolute public URLs (<c>Property.PhotoUrls</c>) whose objects
/// live in the public bucket of <see cref="IFileStorage"/> (FD-07). The first photo is the cover: public pages, search
/// results and the org site show it first. Every method works on one property of the caller's org (tenant filter) and
/// serializes with the other gallery changes of the same property; the caller checks the host permission first
/// (<c>PropertyOperations.Write</c>).
/// </summary>
public interface IPropertyPhotoService
{
    Task<IReadOnlyList<string>> AddAsync(Guid propertyId, IReadOnlyList<IFormFile> files, CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes a photo from the gallery and deletes its object from the storage. A photo of the property's own folder is
    /// deleted from the bucket; an entry that is not an object of this property (a legacy relative path, an external
    /// URL, another property's photo) is only removed from the list, never deleted from the storage. Returns the
    /// gallery after the change.
    /// </summary>
    /// <exception cref="Exceptions.NotFoundException"><c>property_photo_not_found</c>: no such photo in the gallery.</exception>
    Task<IReadOnlyList<string>> DeleteAsync(Guid propertyId, string photoUrl, CancellationToken cancellationToken = default);

    /// <summary>
    /// Sets the order of the gallery: <paramref name="orderedUrls"/> must be exactly the current photos, each once.
    /// Returns the gallery after the change.
    /// </summary>
    /// <exception cref="Exceptions.DomainConflictException"><c>property_photos_changed</c>: the list is not the current gallery.</exception>
    Task<IReadOnlyList<string>> ReorderAsync(Guid propertyId, IReadOnlyList<string> orderedUrls, CancellationToken cancellationToken = default);

    /// <summary>
    /// Makes a photo the cover: it moves first, the others keep their relative order. Idempotent. Returns the gallery
    /// after the change.
    /// </summary>
    /// <exception cref="Exceptions.NotFoundException"><c>property_photo_not_found</c>: no such photo in the gallery.</exception>
    Task<IReadOnlyList<string>> SetCoverAsync(Guid propertyId, string photoUrl, CancellationToken cancellationToken = default);
}
