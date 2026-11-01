using System.Text;

namespace Casazen.Web.Seo;

/// <summary>A link of the chrome of a page: the words and where it goes (an absolute URL on the public site).</summary>
public sealed record SeoLink(string Label, string Url);

/// <summary>A list of links under an optional heading (the groups of «Vedi anche»).</summary>
public sealed record SeoLinkGroup(string? Heading, IReadOnlyList<SeoLink> Links);

/// <summary>
/// What surrounds the text of the editorial pages (guides, tourist tax pages, the hub), as plain HTML for crawlers: the header with
/// the brand and the pages of the site, the breadcrumb as links (until now it existed only as structured data), the block of
/// related pages and the footer with the same links. A crawler page used to be an island with no link in or out except the hub;
/// these links are what lets a crawler (and a person who lands on it) move through the site. Every text is encoded here; the
/// caller passes URLs it built from the public site, never from user input.
/// </summary>
public static class SeoPageChrome
{
    /// <summary>The header of the site: the brand, which leads home, and its navigation.</summary>
    public static string Header(string brand, string homeUrl, string navigationLabel, IReadOnlyList<SeoLink> links)
    {
        var html = new StringBuilder();
        html.Append("<header>\n<p><a href=\"").Append(SeoHtml.Encode(homeUrl)).Append("\">").Append(SeoHtml.Encode(brand)).Append("</a></p>\n");
        AppendNavigation(html, navigationLabel, links);
        html.Append("</header>\n");
        return html.ToString();
    }

    /// <summary>
    /// The breadcrumb as an ordered list of links; the last step is the page itself and is not a link. A step with no URL
    /// (the public site not configured) is text, never a link to nowhere.
    /// </summary>
    public static string Breadcrumb(string label, IReadOnlyList<(string Label, string? Url)> trail)
    {
        if (trail.Count == 0)
            return string.Empty;

        var html = new StringBuilder();
        html.Append("<nav aria-label=\"").Append(SeoHtml.Encode(label)).Append("\">\n<ol>\n");
        for (var index = 0; index < trail.Count; index++)
        {
            var (stepLabel, url) = trail[index];
            var isPage = index == trail.Count - 1;
            if (isPage || string.IsNullOrWhiteSpace(url))
            {
                html.Append(isPage ? "<li aria-current=\"page\">" : "<li>").Append(SeoHtml.Encode(stepLabel)).Append("</li>\n");
                continue;
            }

            html.Append("<li><a href=\"").Append(SeoHtml.Encode(url)).Append("\">").Append(SeoHtml.Encode(stepLabel)).Append("</a></li>\n");
        }

        html.Append("</ol>\n</nav>\n");
        return html.ToString();
    }

    /// <summary>«Vedi anche»: the groups of related pages. Nothing at all when no group has a link.</summary>
    public static string SeeAlso(string heading, IReadOnlyList<SeoLinkGroup> groups)
    {
        var filled = groups.Where(group => group.Links.Count > 0).ToList();
        if (filled.Count == 0)
            return string.Empty;

        var html = new StringBuilder();
        html.Append("<aside>\n<h2>").Append(SeoHtml.Encode(heading)).Append("</h2>\n");
        foreach (var group in filled)
        {
            if (!string.IsNullOrWhiteSpace(group.Heading))
                html.Append("<h3>").Append(SeoHtml.Encode(group.Heading)).Append("</h3>\n");

            html.Append("<ul>\n");
            foreach (var link in group.Links)
            {
                html.Append("<li><a href=\"").Append(SeoHtml.Encode(link.Url)).Append("\">").Append(SeoHtml.Encode(link.Label)).Append("</a></li>\n");
            }

            html.Append("</ul>\n");
        }

        html.Append("</aside>\n");
        return html.ToString();
    }

    /// <summary>The footer of the site: the same links as the header, and one line about what CasaZen is.</summary>
    public static string Footer(string tagline, string navigationLabel, IReadOnlyList<SeoLink> links)
    {
        var html = new StringBuilder();
        html.Append("<footer>\n");
        AppendNavigation(html, navigationLabel, links);
        html.Append("<p>").Append(SeoHtml.Encode(tagline)).Append("</p>\n</footer>\n");
        return html.ToString();
    }

    private static void AppendNavigation(StringBuilder html, string label, IReadOnlyList<SeoLink> links)
    {
        if (links.Count == 0)
            return;

        html.Append("<nav aria-label=\"").Append(SeoHtml.Encode(label)).Append("\">\n<ul>\n");
        foreach (var link in links)
            html.Append("<li><a href=\"").Append(SeoHtml.Encode(link.Url)).Append("\">").Append(SeoHtml.Encode(link.Label)).Append("</a></li>\n");

        html.Append("</ul>\n</nav>\n");
    }
}
