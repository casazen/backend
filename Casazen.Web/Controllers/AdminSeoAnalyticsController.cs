using Casazen.Core.Services;
using Casazen.Web.Authorization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Casazen.Web.Controllers;

[ApiController]
[Route("api/admin/seo/top-comuni")]
[Authorize(Policy = CasazenPolicies.AdminOnly)]
public class AdminSeoAnalyticsController(ISeoEventService seoEventService) : ControllerBase
{
    [HttpGet]
    [ProducesResponseType(typeof(SeoTopComuniResult), StatusCodes.Status200OK)]
    public async Task<ActionResult<SeoTopComuniResult>> GetTopComuni(
        [FromQuery] int days = 30,
        [FromQuery] int limit = 10,
        CancellationToken cancellationToken = default) =>
        Ok(await seoEventService.GetTopComuniAsync(days, limit, cancellationToken));
}
