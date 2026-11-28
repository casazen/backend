namespace Casazen.Web.Seo;

public enum SeoPageStatus
{
    Ok,

    MovedPermanently,

    NotFound,

    UnknownHost,
}

public sealed record SeoPageResult(SeoPageStatus Status, string Html, string? RedirectPath = null);

public sealed record HostSitemap(bool IsOrgHost, string? Xml);

public interface IPublicSeoService
{
    Task<SeoPageResult> RenderOrgAsync(string orgSlug, string? requestHost, CancellationToken cancellationToken = default);

    /// <summary>A property page (<c>/book/{slug}/property/{slug or id}</c>): published properties only, 404 otherwise.</summary>
    Task<SeoPageResult> RenderPropertyAsync(
        string orgSlug, string propertySlugOrId, string? requestHost, CancellationToken cancellationToken = default);

    Task<SeoPageResult> RenderHostPageAsync(string host, string path, CancellationToken cancellationToken = default);

    /// <summary>
    /// The public showcase of an <b>active</b> supplier (<c>/fornitori/{slug}</c>): 404 for an unknown, pending or suspended
    /// one, a 301 to the lowercase slug otherwise.
    /// </summary>
    Task<SeoPageResult> RenderSupplierAsync(string slug, CancellationToken cancellationToken = default);

    Task<string> BuildSupplierSitemapAsync(CancellationToken cancellationToken = default);

    Task<SeoPageResult> RenderGuideAsync(string regionSlug, string comuneSlug, CancellationToken cancellationToken = default);

    Task<SeoPageResult> RenderTouristTaxAsync(string comuneSlug, CancellationToken cancellationToken = default);

    Task<SeoPageResult> RenderHubAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Sitemap of one org: its landing page and its published properties, with absolute URLs on
    /// <c>App:PublicSiteBaseUrl</c>. <c>null</c> when the org is unknown, inactive, reached by an old slug or has nothing
    /// published (such an org is not indexed), and for an org that has its own host (its sitemap is on that host). Throws when
    /// the public URL is not configured (never a fallback domain).
    /// </summary>
    Task<string?> BuildOrgSitemapAsync(string orgSlug, CancellationToken cancellationToken = default);

    Task<HostSitemap> BuildHostSitemapAsync(string host, CancellationToken cancellationToken = default);

    Task<string> BuildOrgSitemapIndexAsync(CancellationToken cancellationToken = default);
}
