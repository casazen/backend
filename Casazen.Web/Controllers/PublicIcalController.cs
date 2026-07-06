using Casazen.Infrastructure.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Casazen.Web.Controllers;

[ApiController]
[Route("api/public/ical")]
[AllowAnonymous]
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
        catch (InvalidOperationException)
        {
            return NotFound();
        }
    }
}
