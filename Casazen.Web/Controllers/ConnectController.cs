using Casazen.Core.Multitenancy;
using Casazen.Core.Services;
using Casazen.Web.DTOs.Connect;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Casazen.Web.Controllers;

[ApiController]
[Route("api/connect")]
[Authorize(Policy = "RequireContext:short-rent:property.write")]
public class ConnectController(
    ITenantContext tenantContext,
    IConnectOnboardingService connectOnboardingService) : ControllerBase
{
    [HttpPost("account")]
    [ProducesResponseType(typeof(ConnectStatusDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ConnectStatusDto>> CreateAccount(CancellationToken cancellationToken)
    {
        var orgId = RequireOrgId();
        if (orgId is null)
            return NotFound(new { error = "No organization assigned to the current user" });

        var status = await connectOnboardingService.EnsureExpressAccountAsync(orgId.Value, cancellationToken);
        return Ok(Map(status));
    }

    [HttpPost("onboarding-link")]
    [ProducesResponseType(typeof(OnboardingLinkResponseDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<OnboardingLinkResponseDto>> CreateOnboardingLink(
        [FromBody] OnboardingLinkRequestDto dto,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(dto.ReturnUrl) || string.IsNullOrWhiteSpace(dto.RefreshUrl))
            return BadRequest(new { error = "returnUrl and refreshUrl are required" });

        var orgId = RequireOrgId();
        if (orgId is null)
            return NotFound(new { error = "No organization assigned to the current user" });

        var url = await connectOnboardingService.CreateOnboardingLinkAsync(
            orgId.Value,
            dto.ReturnUrl,
            dto.RefreshUrl,
            cancellationToken);

        return Ok(new OnboardingLinkResponseDto { Url = url });
    }

    [HttpGet("status")]
    [ProducesResponseType(typeof(ConnectStatusDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ConnectStatusDto>> GetStatus(
        [FromQuery] bool refresh = true,
        CancellationToken cancellationToken = default)
    {
        var orgId = RequireOrgId();
        if (orgId is null)
            return NotFound(new { error = "No organization assigned to the current user" });

        var status = await connectOnboardingService.GetStatusAsync(orgId.Value, refresh, cancellationToken);
        return Ok(Map(status));
    }

    private Guid? RequireOrgId() => tenantContext.OrgId;

    private static ConnectStatusDto Map(ConnectStatus status) => new()
    {
        ConnectedAccountId = status.ConnectedAccountId,
        ChargesEnabled = status.ChargesEnabled,
        PayoutsEnabled = status.PayoutsEnabled,
        DetailsSubmitted = status.DetailsSubmitted,
        RequirementsDue = status.RequirementsDue.ToList(),
    };
}
