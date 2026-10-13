using System.Text.Json.Nodes;

namespace Casazen.Web.Seo;

/// <summary>
/// A public page as a crawler or a link preview reads it (BK-15): the head (title, description, canonical, robots, Open
/// Graph, JSON-LD) and a text version of the content. Rendered by <see cref="SeoHtmlRenderer"/>.
/// </summary>
public sealed class SeoDocument
{
    /// <summary>Language of the page and of its texts (<c>it</c> or <c>en</c>): the <c>lang</c> of the document.</summary>
    public required string Language { get; init; }

    public required string Title { get; init; }

    public string? Description { get; init; }

    /// <summary>
    /// Absolute URL of the page on the public domain, without query string; <c>null</c> only when the public URL is not
    /// configured (Development/Testing), in which case no canonical, <c>og:url</c> or <c>hreflang</c> is written.
    /// </summary>
    public string? CanonicalUrl { get; init; }

    /// <summary>False: <c>noindex,nofollow</c> (not published, empty, unknown or unverified host, not found).</summary>
    public bool Indexable { get; init; } = true;

    /// <summary><c>og:site_name</c>.</summary>
    public string? SiteName { get; init; }

    /// <summary><c>og:type</c>: <c>website</c> for the booking sites, <c>article</c> for the guides.</summary>
    public string OgType { get; init; } = "website";

    /// <summary>Absolute https URL of the preview image (<c>og:image</c>), <c>null</c> when the page has none.</summary>
    public string? ImageUrl { get; init; }

    /// <summary>Structured data blocks, each a complete schema.org object with its own <c>@context</c>.</summary>
    public IReadOnlyList<JsonObject> JsonLd { get; init; } = [];

    /// <summary>
    /// Content of <c>&lt;body&gt;</c>: markup already escaped or sanitized by the code that builds it (see
    /// <see cref="SeoHtml"/>), never raw user text.
    /// </summary>
    public string BodyHtml { get; init; } = string.Empty;
}
