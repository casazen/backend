using Casazen.Core.Services;
using Microsoft.AspNetCore.Http;

namespace Casazen.Web.DTOs.Supplier;

// The images of the supplier's showcase (SP-13b): the logo, the cover and the photos of the jobs the editor uploads. The answer carries the
// address to put into the content (logo.url, cover.url, gallery[].photoUrl or beforeUrl); the content then points to the file by that address.

/// <summary>Form of <c>POST /api/supplier/showcase/media</c> (multipart): what the image is for and the image.</summary>
public sealed class ShowcaseMediaForm
{
    /// <summary>What the image is for: <c>logo</c> (200 KB at most), <c>cover</c>, <c>work</c> (the photo of a job) or <c>before</c> (its "before" photo) (10 MB at most).</summary>
    public string? Kind { get; set; }

    /// <summary>
    /// The image: PNG, JPEG or WebP, judged on its content and not on its name or its declared type (an SVG, a PDF or a text renamed to
    /// <c>.jpg</c> is a 415). It is stored without its metadata (EXIF and GPS position, XMP, comments).
    /// </summary>
    public IFormFile? File { get; set; }
}

/// <summary>An image of the showcase as the upload answers it.</summary>
public sealed class ShowcaseMediaDto
{
    /// <summary>The id to delete it with (<c>DELETE /api/supplier/showcase/media/{id}</c>).</summary>
    public Guid Id { get; set; }

    /// <summary><c>logo</c>, <c>cover</c>, <c>work</c> or <c>before</c>.</summary>
    public string Kind { get; set; } = string.Empty;

    /// <summary>The absolute public address, to put into the content as it is.</summary>
    public string Url { get; set; } = string.Empty;

    /// <summary><c>image/jpeg</c>, <c>image/png</c> or <c>image/webp</c>: what the file is, read from its content.</summary>
    public string ContentType { get; set; } = string.Empty;

    /// <summary>The size of the stored file, after its metadata was removed.</summary>
    public int Bytes { get; set; }

    /// <summary>The width of the picture as a browser draws it; <c>null</c> when it could not be told.</summary>
    public int? Width { get; set; }

    /// <summary>The height of the picture as a browser draws it; <c>null</c> when it could not be told.</summary>
    public int? Height { get; set; }

    public DateTime CreatedAt { get; set; }

    public static ShowcaseMediaDto From(ShowcaseMediaItem item) => new()
    {
        Id = item.Id,
        Kind = item.Kind,
        Url = item.Url,
        ContentType = item.ContentType,
        Bytes = item.Bytes,
        Width = item.Width,
        Height = item.Height,
        CreatedAt = item.CreatedAt,
    };
}
