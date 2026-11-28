using Casazen.Core.GuestPortal;
using Casazen.Web.DTOs.GuestPortal;
using Microsoft.AspNetCore.Mvc;

namespace Casazen.Web.Controllers;

public sealed class GuestPortalOverviewController(IGuestPortalOverviewService overview) : GuestPortalControllerBase
{
    [HttpGet]
    [ProducesResponseType(typeof(GuestPortalOverviewDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status429TooManyRequests)]
    public async Task<ActionResult<GuestPortalOverviewDto>> Get(CancellationToken cancellationToken)
    {
        var view = await overview.GetAsync(Portal, cancellationToken);
        return view is null ? LinkInvalid() : Ok(GuestPortalOverviewDto.From(view));
    }
}
