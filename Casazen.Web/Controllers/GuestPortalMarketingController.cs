using Casazen.Core.GuestPortal;
using Casazen.Core.Services;
using Casazen.Web.DTOs.Crm;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Casazen.Web.Controllers;

public sealed class GuestPortalMarketingController(IPortalMarketingPreferences preferences) : GuestPortalControllerBase
{
    [HttpGet("privacy/marketing")]
    public async Task<IActionResult> Get(CancellationToken ct) =>
        Ok(GuestMarketingDto.From(await preferences.GetAsync(Portal, ct)));

    [HttpPut("privacy/marketing")]
    [EnableRateLimiting(RateLimitPolicies.GuestPortalWrite)]
    [RequestSizeLimit(4096)]
    public async Task<IActionResult> Put(UpdateGuestMarketingRequest request, CancellationToken ct) =>
        Ok(GuestMarketingDto.From(await preferences.SetAsync(Portal, request.ToChange(), ClientIp.GetString(HttpContext), ct)));

    [HttpPost("privacy/marketing/resend-confirmation")]
    [EnableRateLimiting(RateLimitPolicies.GuestPortalWrite)]
    public async Task<IActionResult> Resend(CancellationToken ct)
    {
        var outcome = await preferences.ResendAsync(Portal, ct);
        return outcome == MarketingConfirmationSendOutcome.Unavailable
            ? this.ApiProblem(StatusCodes.Status503ServiceUnavailable, "marketing_confirmation_unavailable", "MarketingConfirmationUnavailable")
            : Accepted(new GuestMarketingResendResponse(outcome == MarketingConfirmationSendOutcome.Queued));
    }
}
