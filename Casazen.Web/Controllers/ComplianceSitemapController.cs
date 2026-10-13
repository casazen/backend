using System.ComponentModel.DataAnnotations;
using Casazen.Core.Services;
using Casazen.Infrastructure.Email;
using Casazen.Web.Seo;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Casazen.Web.Controllers;

/// <summary>
/// Compliance SEO sitemap (AC8). AllowAnonymous and no rate limit: it is read by search engine crawlers.
/// </summary>
/// <remarks>
/// SE-02 (A8-02): crawlers never call this host. The web app serves <c>/sitemap.xml</c> on its own domain through a
/// Vercel function that proxies this endpoint (frontend <c>api/sitemap.ts</c>), and every URL inside is on
/// <c>App:PublicSiteBaseUrl</c>. The old <c>/sitemap-compliance.xml</c> on the API host listed URLs of another host and
/// is gone. Runbook: <c>docs/runbooks/seo-domain.md</c>.
/// </remarks>
[ApiController]
[AllowAnonymous]
public class ComplianceSitemapController(ISeoContentService seoContentService, IPublicSeoService publicSeoService) : ControllerBase
{
    public const string Route = "api/public/sitemap.xml";

    /// <summary>
    /// The sitemap of the web app's guides. With <paramref name="host"/> (BK-16: the Host the crawler used, sent by the web
    /// app's function) a host that serves an org's own site gets that org's sitemap on its own URLs instead, 404 when it has
    /// nothing published; any other host, or no host, gets the guides sitemap as before.
    /// </summary>
    [HttpGet(Route)]
    [Produces("application/xml")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetSitemap([FromQuery, StringLength(253)] string? host, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(host)
            && PublicSiteHosts.Normalize(host) is { } normalizedHost
            && await publicSeoService.BuildHostSitemapAsync(normalizedHost, cancellationToken) is { IsOrgHost: true } hostSitemap)
        {
            return hostSitemap.Xml is null ? NotFound() : Content(hostSitemap.Xml, "application/xml; charset=utf-8");
        }

        var xml = await seoContentService.BuildComplianceSitemapXmlAsync(cancellationToken);
        return Content(xml, "application/xml; charset=utf-8");
    }
}
