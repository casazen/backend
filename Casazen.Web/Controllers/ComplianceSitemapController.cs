using Casazen.Core.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Casazen.Web.Controllers;

/// <summary>
/// Compliance SEO sitemap (AC8). AllowAnonymous for search engine crawlers.
/// </summary>
[ApiController]
[AllowAnonymous]
public class ComplianceSitemapController(ISeoContentService seoContentService) : ControllerBase
{
    [HttpGet("/sitemap-compliance.xml")]
    [Produces("application/xml")]
    public async Task<IActionResult> GetSitemap(CancellationToken cancellationToken)
    {
        var xml = await seoContentService.BuildComplianceSitemapXmlAsync(cancellationToken);
        return Content(xml, "application/xml");
    }
}
