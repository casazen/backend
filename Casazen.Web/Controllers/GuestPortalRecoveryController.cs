using Casazen.Core.Features;
using Casazen.Web.DTOs;
using Casazen.Web.Infrastructure;
using Casazen.Infrastructure.GuestPortal;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Casazen.Web.Controllers;

[ApiController]
[Route("api/public/guest-portal/recover")]
[AllowAnonymous]
[FeatureGate(FeatureFlags.GuestPortal)]
[PrivateAnswer]
public sealed class GuestPortalRecoveryController(GuestPortalRecovery recovery) : ControllerBase
{
    [HttpPost]
    [EnableRateLimiting(RateLimitPolicies.PublicGuestSession)]
    [GuestBookingEmailRateLimit]
    [GuestSessionCodeRateLimit]
    [RequestSizeLimit(4096)]
    public async Task<IActionResult> Recover(GuestBookingLookupRequest request, CancellationToken ct)
    {
        var url = await recovery.FindAsync(request.OrgSlug, request.BookingCode, request.Email, ct);
        if (url is null)
            return this.ApiProblem(StatusCodes.Status404NotFound, "guest_booking_not_found", "GuestBookingNotFound");

        return Ok(new { url });
    }
}
