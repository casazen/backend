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

/// <summary>
/// The integration settings of the activation with Octorate (NF-108, ONB-05, E10/F01/US02/T01): the overview of the connection and of what can be
/// reopened, and the reopen of any part of the plan, so the onboarding is never a one-off (change a sender, add a channel or properties,
/// reconnect after a revocation).
/// </summary>
/// <remarks>
/// <para><b>Policy.</b> <see cref="CasazenPolicies.OrgBillingAdmin"/> on both actions: an org-level decision. The overview counts only the
/// properties the caller reaches (<c>HostScope</c>, AM-03) and only the caller's org; the connection is the org's own.</para>
/// <para><b>Answers.</b> Codes, counts and instants only; never a token or its ciphertext. The overview is private and never cached. A reopen
/// changes no data: it clears the deferral of the part and says where to continue, and the wizard of the part does the work (a sender change goes
/// through the sender switch of E03 and its guards, never through here).</para>
/// <para>Behind <c>Features:OctorateOnboarding</c>: 404 while it is off, before authentication.</para>
/// </remarks>
[ApiController]
[Route("api/onboarding")]
[FeatureGate(FeatureFlags.OctorateOnboarding)]
[Authorize(Policy = CasazenPolicies.OrgBillingAdmin)]
public sealed class OnboardingIntegrationController(
    IOnboardingIntegrationService integration,
    IOrgContextResolver orgContextResolver,
    IHostScopeResolver hostScopeResolver) : ControllerBase
{
    /// <summary>
    /// The connection of the org (with <c>reconnectRequired</c>), the parts of the plan with <c>reopenable</c>, the properties for each sender of
    /// the Alloggiati and ISTAT communications, and the channel states for each publication status.
    /// </summary>
    /// <response code="200">The overview.</response>
    /// <response code="403">The caller has no org, or reaches nothing in it.</response>
    [HttpGet("integration")]
    [PrivateAnswer]
    [ProducesResponseType(typeof(OnboardingIntegrationDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<OnboardingIntegrationDto>> GetOverview(CancellationToken cancellationToken)
    {
        var orgId = await orgContextResolver.GetOrProvisionOrgIdAsync(cancellationToken);
        var scope = orgId is null ? null : await hostScopeResolver.ResolveHostScopeAsync(User, orgId.Value, cancellationToken);
        if (scope is null)
            return this.ApiProblem(StatusCodes.Status403Forbidden, ProblemCodes.Forbidden, "Forbidden");

        return Ok((await integration.GetOverviewAsync(scope.OrgId, scope, cancellationToken)).ToDto());
    }

    /// <summary>
    /// Reopens a part of the plan: its deferral is cleared and its "To do" closed, and the answer says where to continue (the launch target of the
    /// part and, for the compliance wizard, the focus <c>sender</c>). Nothing else is written.
    /// </summary>
    /// <response code="200">Where to continue.</response>
    /// <response code="404"><c>onboarding_part_unknown</c>: the route names no part.</response>
    /// <response code="422"><c>onboarding_part_not_reopenable</c>: nobody provides the part, so there is no wizard to go back to.</response>
    [HttpPost("parts/{key}/reopen")]
    [ProducesResponseType(typeof(OnboardingReopenDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<OnboardingReopenDto>> Reopen(string key, CancellationToken cancellationToken)
    {
        if (!OnboardingPartCatalog.TryParse(key, out var part))
            return this.ApiProblem(StatusCodes.Status404NotFound, OnboardingPlanCodes.PartUnknown, "OnboardingPartUnknown");

        var userId = User.GetUserId();
        var orgId = await orgContextResolver.GetOrProvisionOrgIdAsync(cancellationToken);
        if (orgId is null || string.IsNullOrWhiteSpace(userId))
            return this.ApiProblem(StatusCodes.Status403Forbidden, ProblemCodes.Forbidden, "Forbidden");

        return Ok((await integration.ReopenPartAsync(orgId.Value, userId, part, cancellationToken)).ToDto());
    }
}
