using Casazen.Core.Services;
using Casazen.Web.Authorization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Casazen.Web.Controllers;

/// <summary>
/// Which comuni bring signups (SE-04, #300 AC9): the SEO pages' CTA clicks by comune, for the platform admin widget.
/// Counts only: the events hold no personal data and no list of events is exposed.
/// </summary>
[ApiController]
[Route("api/admin/seo/top-comuni")]
[Authorize(Policy = CasazenPolicies.AdminOnly)]
public class AdminSeoAnalyticsController(ISeoEventService seoEventService) : ControllerBase
{
    /// <summary>
    /// Comuni by CTA clicks in the last <paramref name="days"/> days (default 30, at most the retention period:
    /// <c>Seo:Events:RetentionDays</c>), best first, at most <paramref name="limit"/> (default 10, at most 50), each with
    /// the signups started from it and the host signups attributed to it (SE-03).
    /// </summary>
    [HttpGet]
    [ProducesResponseType(typeof(SeoTopComuniResult), StatusCodes.Status200OK)]
    public async Task<ActionResult<SeoTopComuniResult>> GetTopComuni(
        [FromQuery] int days = 30,
        [FromQuery] int limit = 10,
        CancellationToken cancellationToken = default) =>
        Ok(await seoEventService.GetTopComuniAsync(days, limit, cancellationToken));
}
