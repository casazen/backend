using Microsoft.AspNetCore.Http;

namespace Casazen.Core.Services;

/// <summary>An image of a supplier's showcase as the upload answers it (SP-13b).</summary>
/// <param name="Id">The id to delete it with.</param>
/// <param name="Kind"><c>logo</c>, <c>cover</c>, <c>work</c> or <c>before</c>.</param>
/// <param name="Url">The absolute public address to put into the content (<c>logo.url</c>, <c>cover.url</c>, <c>gallery[].photoUrl</c> or <c>beforeUrl</c>).</param>
/// <param name="ContentType"><c>image/jpeg</c>, <c>image/png</c> or <c>image/webp</c>: what the file is, read from its content.</param>
/// <param name="Bytes">The size of the file as stored, after its metadata was removed.</param>
/// <param name="Width">The width of the picture as a browser draws it; <c>null</c> when it could not be told.</param>
/// <param name="Height">The height of the picture as a browser draws it; <c>null</c> when it could not be told.</param>
/// <param name="CreatedAt">When the file was stored (UTC).</param>
public sealed record ShowcaseMediaItem(
    Guid Id,
    string Kind,
    string Url,
    string ContentType,
    int Bytes,
    int? Width,
    int? Height,
    DateTime CreatedAt);

/// <summary>What one run of the cleanup of the images of the showcases did.</summary>
/// <param name="Skipped">Another run was in progress: nothing was done.</param>
/// <param name="Removed">The files removed (the object and its row).</param>
public sealed record ShowcaseMediaCleanupResult(bool Skipped, int Removed)
{
    public static ShowcaseMediaCleanupResult SkippedRun { get; } = new(true, 0);
}

/// <summary>
/// The images of a supplier's showcase (SP-13b, <c>api/supplier/showcase/media</c>): the logo, the cover and the photos of the jobs the
/// supplier uploads from the editor, and the daily cleanup of the ones nobody uses. Every method takes the supplier org (the caller's own
/// supplier link, never a value of the request) and reads and writes only the rows of that org.
/// </summary>
/// <remarks>
/// <para><b>What is checked.</b> The kind and the size first (a logo of 200 KB at most, a photo of 10 MB), then <b>the content</b>: the type is the
/// one the first bytes of the file show (PNG, JPEG or WebP: an SVG, a PDF or a text renamed to <c>.jpg</c> is a 415 whatever its name and its
/// declared type), the file is read through and written again <b>without its metadata</b> (EXIF with the GPS position, XMP, comments, thumbnails,
/// whatever follows the image), and the picture must be one a browser can draw, not animated and not too large in pixels. Nothing is stored for a
/// request that fails any of them. The name of the object is random.</para>
/// <para><b>What the server does not do.</b> It does not decode and recompress the picture (no imaging library is part of the application): the editor
/// makes a photo smaller before it sends it, the server keeps what it received, minus the metadata. The orientation of a photo taken on its side
/// stays as the single piece of metadata that is kept (a minimal EXIF block with the orientation and nothing else), so it is drawn upright.</para>
/// <para><b>Concurrency.</b> The limit and the removal take the supplier's content lock (the one of the draft), so two uploads never pass the limit
/// together and a save never lands between the check that a file is unused and its removal.</para>
/// </remarks>
public interface ISupplierShowcaseMediaService
{
    /// <summary>
    /// Stores an image of the supplier's showcase and returns it. <paramref name="kind"/> is the text of the form (<c>logo</c>, <c>cover</c>,
    /// <c>work</c> or <c>before</c>).
    /// </summary>
    /// <exception cref="Exceptions.DomainRuleException">
    /// 422 <c>showcase_media_none</c>, <c>_kind_invalid</c>, <c>_invalid_image</c>, <c>_dimensions</c> or <c>_limit_reached</c>.
    /// </exception>
    /// <exception cref="Exceptions.DomainPayloadTooLargeException">413 <c>showcase_media_too_large</c>.</exception>
    /// <exception cref="Exceptions.DomainUnsupportedMediaTypeException">415 <c>showcase_media_unsupported_type</c>.</exception>
    Task<ShowcaseMediaItem> UploadAsync(Guid supplierOrgId, string? kind, IFormFile? file, CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes an image of the supplier (the object and its row) when nothing refers to it: not the draft, not the published version, not the one
    /// published before it.
    /// </summary>
    /// <exception cref="Exceptions.NotFoundException"><c>showcase_media_not_found</c>: no image of this supplier has this id.</exception>
    /// <exception cref="Exceptions.DomainConflictException"><c>showcase_media_in_use</c>.</exception>
    Task DeleteAsync(Guid supplierOrgId, Guid mediaId, CancellationToken cancellationToken = default);

    /// <summary>
    /// The daily cleanup (job <c>supplier-showcase-media-cleanup</c>): removes the images older than
    /// <see cref="Suppliers.ShowcaseMediaLimits.OrphanGrace"/> that neither the draft, nor the published version, nor the one before it refers to.
    /// Idempotent: a second run finds nothing more. Never removes a file in use.
    /// </summary>
    Task<ShowcaseMediaCleanupResult> CleanupAsync(CancellationToken cancellationToken = default);
}
