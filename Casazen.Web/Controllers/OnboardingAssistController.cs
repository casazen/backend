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
/// «Do it for me» of the Octorate activation (UX-15, NF-15, NF-107, E10/F01/US03/T01): the host asks CasaZen support to do a part, or a step of the
/// Octorate panel, for it. The request keeps a reference to the host's wizard draft (never a copy), raises the host's "To do" and writes the line of
/// the organization log; support handles it from the staff queue (<see cref="AdminOnboardingAssistController"/>).
/// </summary>
/// <remarks>
/// <para><b>Policy.</b> <see cref="CasazenPolicies.OrgBillingAdmin"/>: an org-level decision. The requests are bound to the caller's org and to what
/// the caller reaches (<c>HostScope</c>, AM-03): a property the caller does not reach is a 404, a request about the whole org needs a scope over the
/// whole org.</para>
/// <para>Behind <c>Features:OctorateOnboarding</c>: 404 while it is off, before authentication.</para>
/// </remarks>
[ApiController]
[Route("api/onboarding/assist-requests")]
[FeatureGate(FeatureFlags.OctorateOnboarding)]
[Authorize(Policy = CasazenPolicies.OrgBillingAdmin)]
public sealed class OnboardingAssistController(
    IOnboardingAssistService assist,
    IOrgContextResolver orgContextResolver,
    IHostScopeResolver hostScopeResolver) : ControllerBase
{
    /// <summary>Asks CasaZen support to do a part, or a panel step, for the host.</summary>
    /// <response code="200">The request, <c>Requested</c>, with its "To do".</response>
    /// <response code="404"><c>onboarding_property_not_found</c>.</response>
    /// <response code="409"><c>assist_request_already_open</c> (a request of the same part and property is open), <c>assist_step_already_done</c>.</response>
    /// <response code="422"><c>onboarding_part_unknown</c>, <c>assist_context_invalid</c>, <c>panel_step_channel_invalid</c>.</response>
    [HttpPost]
    [PrivateAnswer]
    [ProducesResponseType(typeof(AssistRequestDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<AssistRequestDto>> Create([FromBody] CreateAssistRequestDto? body, CancellationToken cancellationToken)
    {
        var userId = User.GetUserId();
        var scope = await ScopeAsync(cancellationToken);
        if (scope is null || string.IsNullOrWhiteSpace(userId))
            return this.ApiProblem(StatusCodes.Status403Forbidden, ProblemCodes.Forbidden, "Forbidden");

        if (!OnboardingPartCatalog.TryParse(body?.Part, out var part))
            return this.ApiProblem(StatusCodes.Status422UnprocessableEntity, OnboardingPlanCodes.PartUnknown, "OnboardingPartUnknown");

        var input = new AssistRequestInput(part, body?.Context ?? string.Empty, body?.PropertyId, body?.ChannelCode);
        return Ok((await assist.RequestAsync(scope, userId, input, cancellationToken)).ToDto());
    }

    /// <summary>The requests of the org the caller reaches, the most recent first, with their status and the note of support.</summary>
    /// <response code="200">The requests.</response>
    [HttpGet]
    [PrivateAnswer]
    [ProducesResponseType(typeof(List<AssistRequestDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<List<AssistRequestDto>>> List(CancellationToken cancellationToken)
    {
        var scope = await ScopeAsync(cancellationToken);
        if (scope is null)
            return this.ApiProblem(StatusCodes.Status403Forbidden, ProblemCodes.Forbidden, "Forbidden");

        return Ok((await assist.ListAsync(scope, cancellationToken)).Select(request => request.ToDto()).ToList());
    }

    private async Task<HostScope?> ScopeAsync(CancellationToken cancellationToken)
    {
        var orgId = await orgContextResolver.GetOrProvisionOrgIdAsync(cancellationToken);
        return orgId is null ? null : await hostScopeResolver.ResolveHostScopeAsync(User, orgId.Value, cancellationToken);
    }
}
