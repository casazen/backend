using System.Security.Claims;
using Casazen.Core.Services;
using Casazen.Web.Mapping;
using Casazen.Web.DTOs.Onboarding;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Casazen.Web.Controllers;

[ApiController]
[Route("api/onboarding")]
[Authorize]
public class OnboardingController(IOnboardingService onboardingService) : ControllerBase
{
    /// <summary>Returns the activation checklist for the caller's organization.</summary>
    [HttpGet("status")]
    [ProducesResponseType(typeof(OnboardingStatusDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<OnboardingStatusDto>> GetStatus(CancellationToken cancellationToken)
    {
        var sub = GetSub();
        if (sub is null)
            return Unauthorized();

        var status = await onboardingService.GetActivationStatusAsync(sub, cancellationToken);
        return Ok(status.ToDto());
    }

    private string? GetSub() =>
        User.FindFirst("sub")?.Value
        ?? User.FindFirst(ClaimTypes.NameIdentifier)?.Value
        ?? User.FindFirst("http://schemas.xmlsoap.org/ws/2005/05/identity/claims/nameidentifier")?.Value;
}
