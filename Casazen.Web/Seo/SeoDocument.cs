using System.Text.Json.Nodes;

namespace Casazen.Web.Seo;

public sealed class SeoDocument
{
    public required string Language { get; init; }

    public required string Title { get; init; }

    public string? Description { get; init; }

    public string? CanonicalUrl { get; init; }

    public bool Indexable { get; init; } = true;

    public string? SiteName { get; init; }

    public string OgType { get; init; } = "website";

    public string? ImageUrl { get; init; }

    public string? TwitterCard { get; init; }

    public IReadOnlyList<JsonObject> JsonLd { get; init; } = [];

    public string BodyHtml { get; init; } = string.Empty;
}
