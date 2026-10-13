using System.ComponentModel.DataAnnotations;
using Casazen.Web.Seo;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Casazen.Web.Controllers;

/// <summary>
/// Sitemaps of the org booking sites (BK-15, A3-20, spec-branded-booking-site AC14). Anonymous and without a rate
/// limiter, like the compliance sitemap (SE-02): search engine crawlers read them, through the web app (frontend
/// <c>api/sitemap.ts</c>), never directly. Every URL inside is on <c>App:PublicSiteBaseUrl</c> (decision D3).
/// </summary>
[ApiController]
[AllowAnonymous]
public class PublicOrgSitemapController(IPublicSeoService seoService) : ControllerBase
{
    /// <summary>
    /// Sitemap index: one sitemap per org that has a published property (<c>/book/{slug}/sitemap.xml</c> on the web
    /// app). Declared in <c>robots.txt</c> next to the compliance sitemap.
    /// </summary>
    [HttpGet("api/public/sitemap-book.xml")]
    [Produces("application/xml")]
    public async Task<IActionResult> GetIndex(CancellationToken cancellationToken) =>
        Xml(await seoService.BuildOrgSitemapIndexAsync(cancellationToken));

    /// <summary>
    /// Sitemap of one org: its landing page and its published properties. 404 for an unknown or inactive org, an old
    /// slug (the sitemap of the current slug is the one to read) and an org with nothing published: it is not indexed.
    /// </summary>
    [HttpGet("api/public/orgs/{orgSlug}/sitemap.xml")]
    [Produces("application/xml")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetOrgSitemap(
        [RegularExpression("^[A-Za-z0-9-]{1,100}$")] string orgSlug,
        CancellationToken cancellationToken)
    {
        var xml = await seoService.BuildOrgSitemapAsync(orgSlug, cancellationToken);
        return xml is null ? NotFound() : Xml(xml);
    }

    private ContentResult Xml(string xml) => Content(xml, "application/xml; charset=utf-8");
}
