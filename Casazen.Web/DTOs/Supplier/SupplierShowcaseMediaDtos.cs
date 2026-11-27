using Casazen.Core.Services;
using Microsoft.AspNetCore.Http;

namespace Casazen.Web.DTOs.Supplier;

public sealed class ShowcaseMediaForm
{
    public string? Kind { get; set; }

    public IFormFile? File { get; set; }
}

public sealed class ShowcaseMediaDto
{
    public Guid Id { get; set; }

    public string Kind { get; set; } = string.Empty;

    public string Url { get; set; } = string.Empty;

    public string ContentType { get; set; } = string.Empty;

    public int Bytes { get; set; }

    public int? Width { get; set; }

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
