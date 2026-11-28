using System.ComponentModel.DataAnnotations;
using Casazen.Core.Services;

namespace Casazen.Web.DTOs;

public sealed class PropertyPhotosResponse
{
    public IReadOnlyList<string> PhotoUrls { get; init; } = [];

    public int MaxPhotos { get; init; } = PropertyPhotoLimits.MaxPhotos;

    public int MaxFilesPerRequest { get; init; } = PropertyPhotoLimits.MaxFilesPerRequest;

    public long MaxFileSizeBytes { get; init; } = PropertyPhotoLimits.MaxFileSizeBytes;

    public IReadOnlyList<string> AllowedContentTypes { get; init; } = PropertyPhotoLimits.AllowedContentTypes;

    public static PropertyPhotosResponse From(IEnumerable<string> photoUrls) => new() { PhotoUrls = [.. photoUrls] };
}

public sealed class SetPropertyCoverPhotoRequest
{
    [Required(ErrorMessage = "PropertyPhotoUrlRequired")]
    [MaxLength(PropertyPhotoRequestLimits.UrlMaxLength, ErrorMessage = "PropertyPhotoUrlTooLong")]
    public string Url { get; set; } = string.Empty;
}

public static class PropertyPhotoRequestLimits
{
    public const int UrlMaxLength = 2048;
}
