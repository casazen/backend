using System.ComponentModel.DataAnnotations;
using Casazen.Core.Services;

namespace Casazen.Web.DTOs;

/// <summary>
/// The photo gallery of a property (PC-04, A2-26): the photos in display order (absolute public URLs of the storage; the
/// first one is the cover that public pages, search results and the org site show first) and the rules the upload
/// enforces, so the web form states the same limits as the server.
/// </summary>
public sealed class PropertyPhotosResponse
{
    /// <summary>Photos in display order; the first is the cover.</summary>
    public IReadOnlyList<string> PhotoUrls { get; init; } = [];

    /// <summary>Photos a property can have.</summary>
    public int MaxPhotos { get; init; } = PropertyPhotoLimits.MaxPhotos;

    /// <summary>Photos accepted by one upload request.</summary>
    public int MaxFilesPerRequest { get; init; } = PropertyPhotoLimits.MaxFilesPerRequest;

    /// <summary>Largest accepted photo, in bytes.</summary>
    public long MaxFileSizeBytes { get; init; } = PropertyPhotoLimits.MaxFileSizeBytes;

    /// <summary>Accepted content types.</summary>
    public IReadOnlyList<string> AllowedContentTypes { get; init; } = PropertyPhotoLimits.AllowedContentTypes;

    public static PropertyPhotosResponse From(IEnumerable<string> photoUrls) => new() { PhotoUrls = [.. photoUrls] };
}

/// <summary>Body of <c>PUT /api/properties/{id}/images/cover</c>.</summary>
public sealed class SetPropertyCoverPhotoRequest
{
    /// <summary>URL of a photo of the gallery, as returned by the API.</summary>
    [Required(ErrorMessage = "PropertyPhotoUrlRequired")]
    [MaxLength(PropertyPhotoRequestLimits.UrlMaxLength, ErrorMessage = "PropertyPhotoUrlTooLong")]
    public string Url { get; set; } = string.Empty;
}

/// <summary>Bounds of the photo URLs of the requests (a stored URL is far shorter).</summary>
public static class PropertyPhotoRequestLimits
{
    public const int UrlMaxLength = 2048;
}
