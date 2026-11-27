using Casazen.Core.GuestPortal;
using Casazen.Web.DTOs.GuestPortal;
using Microsoft.AspNetCore.Mvc;

namespace Casazen.Web.Controllers;

/// <summary>
/// The overview of the guest portal (E50/F01/US01/T02, NF-153, AC-NF153-1): <c>GET api/public/guest-portal/{token}</c>, the first call of the portal
/// page. The stay summary (name and town of the house, days, nights, guests, status, booking code, kind of booking), the brand of the host, the
/// language, the sections with their state, the next step, <c>canSeeReserved</c>, <c>checkInCompleted</c> and the features. Nothing reserved (the
/// precise address, the access, the door code, the Wi-Fi) is ever in it. Everything else (gate, anonymity, rate limit, the uniform 404, private
/// answers) comes from <see cref="GuestPortalControllerBase"/>.
/// </summary>
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
