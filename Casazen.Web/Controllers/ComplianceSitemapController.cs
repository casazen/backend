using Casazen.Core.Services;
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
public class ComplianceSitemapController(ISeoContentService seoContentService) : ControllerBase
{
    public const string Route = "api/public/sitemap.xml";

    [HttpGet(Route)]
    [Produces("application/xml")]
    public async Task<IActionResult> GetSitemap(CancellationToken cancellationToken)
    {
        var xml = await seoContentService.BuildComplianceSitemapXmlAsync(cancellationToken);
        return Content(xml, "application/xml; charset=utf-8");
    }
}
