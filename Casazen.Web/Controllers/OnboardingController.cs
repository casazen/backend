using System.Security.Claims;
using Casazen.Core.Authorization;
using Casazen.Core.Features;
using Casazen.Core.Services;
using Casazen.Web.Authorization;
using Casazen.Web.DTOs.Onboarding;
using Casazen.Web.Infrastructure;
using Casazen.Web.Mapping;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Casazen.Web.Controllers;

[ApiController]
[Route("api/onboarding")]
[Authorize]
public class OnboardingController(
    IOnboardingService onboardingService,
    IFeatureFlags featureFlags,
    IAuthorizationService authorizationService,
    IOrgContextResolver orgContextResolver,
    IHostScopeResolver hostScopeResolver) : ControllerBase
{
    /// <summary>
    /// Returns the activation checklist for the caller's organization. With <c>Features:OctorateOnboarding</c> on and a caller who may read
    /// the short-stay side of the properties (<see cref="CasazenPolicies.PropertyRead"/>), <c>flows</c> also carries the summary of the six
    /// flows over the properties the caller reaches (NF-106); otherwise it is empty and the answer is what it always was. The endpoint is
    /// never behind the flag: it is needed before any context exists.
    /// </summary>
    [HttpGet("status")]
    [ProducesResponseType(typeof(OnboardingStatusDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<OnboardingStatusDto>> GetStatus(CancellationToken cancellationToken)
    {
        var sub = GetSub();
        if (sub is null)
            return Unauthorized();

        var status = await onboardingService.GetActivationStatusAsync(sub, cancellationToken);
        var dto = status.ToDto();
        dto.Flows = await FlowSummariesAsync(cancellationToken);
        return Ok(dto);
    }

    /// <summary>
    /// The summary of the six flows, or nothing: the flag is off, the caller may not read the properties of a short-stay context, or it has no
    /// org or reaches nothing in it. The flag is asked first, so with it off nothing else is read.
    /// </summary>
    private async Task<List<FlowSummaryDto>> FlowSummariesAsync(CancellationToken cancellationToken)
    {
        if (!featureFlags.IsEnabled(FeatureFlags.OctorateOnboarding)
            || !(await authorizationService.AuthorizeAsync(User, CasazenPolicies.PropertyRead)).Succeeded
            || await orgContextResolver.GetOrProvisionOrgIdAsync(cancellationToken) is not { } orgId
            || await hostScopeResolver.ResolveHostScopeAsync(User, orgId, cancellationToken) is not { } scope)
        {
            return [];
        }

        // Resolved here and not in the constructor: with the flag off (and for a caller who may not read the properties) the graph of the
        // flow status, which reaches the Octorate mapping service, is never built, so this endpoint stays what it was.
        var flowStatus = HttpContext.RequestServices.GetRequiredService<IOnboardingFlowStatusService>();
        return [.. (await flowStatus.GetSummariesAsync(scope, cancellationToken)).Select(summary => summary.ToDto())];
    }

    private string? GetSub() =>
        User.FindFirst("sub")?.Value
        ?? User.FindFirst(ClaimTypes.NameIdentifier)?.Value
        ?? User.FindFirst("http://schemas.xmlsoap.org/ws/2005/05/identity/claims/nameidentifier")?.Value;
}
