using Casazen.Core.Exceptions;
using Casazen.Infrastructure.Services;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Casazen.Web.Controllers;

[ApiController]
[Route("api/public/ical")]
[AllowAnonymous]
[EnableRateLimiting(RateLimitPolicies.PublicIcal)]
public class PublicIcalController(PropertyICalSyncService syncService) : ControllerBase
{
    [HttpGet("{exportToken:guid}")]
    [Produces("text/calendar")]
    public async Task<IActionResult> GetExportFeed(Guid exportToken, CancellationToken cancellationToken)
    {
        try
        {
            var ics = await syncService.BuildPublicExportAsync(exportToken, cancellationToken);
            return Content(ics, "text/calendar; charset=utf-8");
        }
        catch (NotFoundException)
        {
            return NotFound();
        }
    }
}
