namespace Casazen.Infrastructure.Email;

/// <summary>
/// Paths of the public booking site in the web app (frontend routes <c>/book/:orgSlug/…</c>), the single place where the
/// backend writes them: the canonical URLs, the sitemaps and the crawler pages of the booking sites (BK-15) use this
/// class only, so they cannot disagree with each other. The absolute URL is always <c>App:PublicSiteBaseUrl</c> + path
/// (<see cref="PublicSiteLinks.PublicPage"/>), never a domain written here (decision D3).
/// </summary>
public static class PublicSitePaths
{
    /// <summary>Sitemap index of the booking sites: one entry per org that has something to index (BK-15).</summary>
    public const string OrgSitemapIndex = "/sitemap-book.xml";

    /// <summary>Landing page of an org's booking site (<c>/book/{slug}</c>).</summary>
    public static string Org(string orgSlug)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(orgSlug);
        return $"/book/{Uri.EscapeDataString(orgSlug)}";
    }

    /// <summary>Public page of a property: <c>/book/{orgSlug}/property/{slug or id}</c> (the id only when it has no slug).</summary>
    public static string Property(string orgSlug, string propertySlugOrId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(propertySlugOrId);
        return $"{Org(orgSlug)}/property/{Uri.EscapeDataString(propertySlugOrId)}";
    }

    /// <summary>
    /// Sitemap of one org (<c>/book/{slug}/sitemap.xml</c>): under the org's own path, so it may only list URLs of that
    /// path, as the sitemap protocol requires.
    /// </summary>
    public static string OrgSitemap(string orgSlug) => $"{Org(orgSlug)}/sitemap.xml";
}
