namespace Casazen.Web.Seo;

/// <summary>How a crawler page request ends: the page, a permanent redirect to its canonical path, or "not found".</summary>
public enum SeoPageStatus
{
    Ok,

    /// <summary>The page exists under another path (an old org slug, a property reached by id): 301 to <see cref="SeoPageResult.RedirectPath"/>.</summary>
    MovedPermanently,

    /// <summary>A real 404 for crawlers (the single-page app can only answer 200 with a "not found" screen: soft 404).</summary>
    NotFound,
}

/// <param name="Html">The document to serve; for a redirect, a minimal one; for "not found", a noindex page.</param>
/// <param name="RedirectPath">Same-host path of the canonical page, only for <see cref="SeoPageStatus.MovedPermanently"/>.</param>
public sealed record SeoPageResult(SeoPageStatus Status, string Html, string? RedirectPath = null);

/// <summary>
/// The pages of the public site as crawlers and link previews read them, and the sitemaps of the booking sites (BK-15).
/// </summary>
public interface IPublicSeoService
{
    /// <summary>Landing page of an org's booking site (<c>/book/{slug}</c>).</summary>
    /// <param name="requestHost">Host the crawler used (the Host header the web app received); <c>null</c> when unknown.</param>
    Task<SeoPageResult> RenderOrgAsync(string orgSlug, string? requestHost, CancellationToken cancellationToken = default);

    /// <summary>A property page (<c>/book/{slug}/property/{slug or id}</c>): published properties only, 404 otherwise.</summary>
    Task<SeoPageResult> RenderPropertyAsync(
        string orgSlug, string propertySlugOrId, string? requestHost, CancellationToken cancellationToken = default);

    /// <summary>A compliance guide (<c>/p/affitti-brevi/{region}/{comune}</c>).</summary>
    Task<SeoPageResult> RenderGuideAsync(string regionSlug, string comuneSlug, CancellationToken cancellationToken = default);

    /// <summary>A tourist tax page (<c>/p/tassa-soggiorno/{comune}</c>).</summary>
    Task<SeoPageResult> RenderTouristTaxAsync(string comuneSlug, CancellationToken cancellationToken = default);

    /// <summary>The hub of the guides (<c>/p/affitti-brevi</c>).</summary>
    Task<SeoPageResult> RenderHubAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Sitemap of one org: its landing page and its published properties, with absolute URLs on
    /// <c>App:PublicSiteBaseUrl</c>. <c>null</c> when the org is unknown, inactive, reached by an old slug or has nothing
    /// published (such an org is not indexed). Throws when the public URL is not configured (never a fallback domain).
    /// </summary>
    Task<string?> BuildOrgSitemapAsync(string orgSlug, CancellationToken cancellationToken = default);

    /// <summary>
    /// Sitemap index of the booking sites: the sitemap of every org that has a published property. Throws when the
    /// public URL is not configured.
    /// </summary>
    Task<string> BuildOrgSitemapIndexAsync(CancellationToken cancellationToken = default);
}
