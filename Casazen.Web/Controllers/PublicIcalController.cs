using Casazen.Core.Exceptions;
using Casazen.Infrastructure.Services;
using Casazen.Web.Infrastructure;
using Casazen.Web.Resources;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Localization;

namespace Casazen.Web.Controllers;

/// <summary>
/// Public iCal export of a property, polled by the OTAs (PC-12, A2-22). Anonymous: the unguessable token of the link is
/// the only credential; an unknown or regenerated token answers 404. Rate limited per client IP
/// (<see cref="RateLimitPolicies.PublicIcal"/>, FD-10). Content and format: <see cref="ICalExportService"/>.
/// </summary>
[ApiController]
[Route("api/public/ical")]
[AllowAnonymous]
[EnableRateLimiting(RateLimitPolicies.PublicIcal)]
public class PublicIcalController(
    PropertyICalSyncService syncService,
    IStringLocalizer<SharedResources> localizer) : ControllerBase
{
    [HttpGet("{exportToken:guid}")]
    [Produces("text/calendar")]
    public async Task<IActionResult> GetExportFeed(Guid exportToken, CancellationToken cancellationToken)
    {
        try
        {
            // Neutral SUMMARY in the request language (Italian by default): never a name or a note.
            var ics = await syncService.BuildPublicExportAsync(
                exportToken, localizer["ICalExportBusySummary"], cancellationToken);
            return Content(ics, "text/calendar; charset=utf-8");
        }
        catch (NotFoundException)
        {
            return NotFound();
        }
    }
}
