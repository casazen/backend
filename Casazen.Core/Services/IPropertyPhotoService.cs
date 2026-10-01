using Microsoft.AspNetCore.Http;

namespace Casazen.Core.Services;

/// <summary>Rules of the photo gallery of a property (PC-04, A2-26).</summary>
public static class PropertyPhotoLimits
{
    /// <summary>Photos a property can have in its gallery.</summary>
    public const int MaxPhotos = 20;

    /// <summary>Photos accepted by one upload request (the 413 limit of the endpoint follows from it).</summary>
    public const int MaxFilesPerRequest = 10;

    /// <summary>Largest accepted photo, in bytes (10 MB).</summary>
    public const long MaxFileSizeBytes = 10 * 1024 * 1024;

    /// <summary>Content types of the accepted photos; each file is also checked on its content, not only on its header.</summary>
    public static readonly IReadOnlyList<string> AllowedContentTypes = ["image/jpeg", "image/png", "image/webp"];

    /// <summary>Upper bound of one upload request: <see cref="MaxFilesPerRequest"/> photos plus the multipart overhead.</summary>
    public const long MaxRequestBytes = MaxFilesPerRequest * MaxFileSizeBytes + 1024 * 1024;

    /// <summary>422: the upload carries no file.</summary>
    public const string NoFileCode = "property_photo_none";

    /// <summary>422: more files in one request than <see cref="MaxFilesPerRequest"/>.</summary>
    public const string TooManyFilesCode = "property_photo_too_many_files";

    /// <summary>422: the gallery would exceed <see cref="MaxPhotos"/>.</summary>
    public const string LimitReachedCode = "property_photo_limit_reached";

    /// <summary>422: not a JPEG, PNG or WebP image (extension, declared type or content).</summary>
    public const string InvalidTypeCode = "property_photo_invalid_type";

    /// <summary>422: empty file or larger than <see cref="MaxFileSizeBytes"/>.</summary>
    public const string InvalidSizeCode = "property_photo_invalid_size";

    /// <summary>404: the photo is not (or no longer) in the gallery of the property.</summary>
    public const string NotFoundCode = "property_photo_not_found";

    /// <summary>409: the order sent is not the current gallery (another tab or user changed it meanwhile).</summary>
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
    /// <summary>
    /// Validates and stores <paramref name="files"/> (all or none) and appends them to the gallery, in the order sent.
    /// Returns the gallery after the change.
    /// </summary>
    /// <exception cref="Exceptions.DomainRuleException">
    /// <c>property_photo_none</c>, <c>property_photo_too_many_files</c>, <c>property_photo_invalid_type</c>,
    /// <c>property_photo_invalid_size</c> or <c>property_photo_limit_reached</c>.
    /// </exception>
    /// <exception cref="Exceptions.NotFoundException">The property does not exist in the caller's org.</exception>
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
