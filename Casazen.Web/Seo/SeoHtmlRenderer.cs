using System.Text;

namespace Casazen.Web.Seo;

/// <summary>
/// Writes a <see cref="SeoDocument"/> as the HTML document that crawlers and link previews read (BK-15): title,
/// description, <c>robots</c>, canonical, <c>hreflang</c>, Open Graph, Twitter card and JSON-LD in the head, the text of
/// the page in the body. It has no script and no stylesheet: it is what the web app serves to crawlers instead of the
/// empty <c>index.html</c> of the single-page app (frontend <c>api/seo.ts</c>), which people keep getting.
/// </summary>
public static class SeoHtmlRenderer
{
    /// <summary><c>robots</c> of an indexable page; the large image preview lets link previews show the photo.</summary>
    public const string RobotsIndex = "index,follow,max-image-preview:large";

    /// <summary><c>robots</c> of a page that must not be indexed.</summary>
    public const string RobotsNoIndex = "noindex,nofollow";

    public static string Render(SeoDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);

        var html = new StringBuilder(2048);
        html.Append("<!doctype html>\n");
        html.Append("<html lang=\"").Append(SeoHtml.Encode(document.Language)).Append("\">\n<head>\n");
        html.Append("<meta charset=\"utf-8\">\n");
        html.Append("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">\n");
        html.Append("<title>").Append(SeoHtml.Encode(document.Title)).Append("</title>\n");

        var description = string.IsNullOrWhiteSpace(document.Description) ? null : SeoHtml.Collapse(document.Description);
        if (description is not null)
            AppendMeta(html, "name", "description", description);

        AppendMeta(html, "name", "robots", document.Indexable ? RobotsIndex : RobotsNoIndex);

        // The canonical and the language alternates only on a page that may be indexed: a noindex page points nowhere.
        if (document.CanonicalUrl is not null && document.Indexable)
        {
            html.Append("<link rel=\"canonical\" href=\"").Append(SeoHtml.Encode(document.CanonicalUrl)).Append("\">\n");
            // One URL per page, whatever the language of the visitor: the page is its own language alternate.
            AppendAlternate(html, document.Language, document.CanonicalUrl);
            AppendAlternate(html, "x-default", document.CanonicalUrl);
        }

        AppendMeta(html, "property", "og:type", document.OgType);
        AppendMeta(html, "property", "og:title", document.Title);
        if (description is not null)
            AppendMeta(html, "property", "og:description", description);
        if (document.CanonicalUrl is not null && document.Indexable)
            AppendMeta(html, "property", "og:url", document.CanonicalUrl);
        if (!string.IsNullOrWhiteSpace(document.SiteName))
            AppendMeta(html, "property", "og:site_name", document.SiteName);
        AppendMeta(html, "property", "og:locale", OgLocale(document.Language));
        if (document.ImageUrl is not null)
            AppendMeta(html, "property", "og:image", document.ImageUrl);

        AppendMeta(html, "name", "twitter:card", document.ImageUrl is null ? "summary" : "summary_large_image");
        AppendMeta(html, "name", "twitter:title", document.Title);
        if (description is not null)
            AppendMeta(html, "name", "twitter:description", description);
        if (document.ImageUrl is not null)
            AppendMeta(html, "name", "twitter:image", document.ImageUrl);

        foreach (var block in document.JsonLd)
            html.Append("<script type=\"application/ld+json\">").Append(SeoJsonLd.Serialize(block)).Append("</script>\n");

        html.Append("</head>\n<body>\n");
        html.Append(document.BodyHtml);
        html.Append("</body>\n</html>\n");
        return html.ToString();
    }

    private static void AppendMeta(StringBuilder html, string attribute, string name, string content) =>
        html.Append("<meta ").Append(attribute).Append("=\"").Append(name).Append("\" content=\"")
            .Append(SeoHtml.Encode(content)).Append("\">\n");

    private static void AppendAlternate(StringBuilder html, string hreflang, string href) =>
        html.Append("<link rel=\"alternate\" hreflang=\"").Append(SeoHtml.Encode(hreflang)).Append("\" href=\"")
            .Append(SeoHtml.Encode(href)).Append("\">\n");

    private static string OgLocale(string language) => language == "en" ? "en_US" : "it_IT";
}
