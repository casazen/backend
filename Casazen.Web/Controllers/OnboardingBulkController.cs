using Casazen.Core.Authorization;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Features;
using Casazen.Core.Services;
using Casazen.Core.Validation;
using Casazen.Web.Authorization;
using Casazen.Web.DTOs.Onboarding;
using Casazen.Web.Infrastructure;
using Casazen.Web.Mapping;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Casazen.Web.Controllers;

/// <summary>
/// The bulk configuration of an agency with many properties (NF-105, ONB-02, E10/F02/US01/T01): a choice made once and applied to all the
/// properties or to a selection, how many are ready («142 of 250»), and the progressive activation, a pilot group first and then the others (H10).
/// </summary>
/// <remarks>
/// <para><b>Policy.</b> <see cref="CasazenPolicies.OrgBillingAdmin"/>: an org-level decision. Every property is bound in SQL to the caller's org and
/// to what it reaches (<c>HostScope</c>, AM-03); a property of another org, one outside the reach and one the flows do not apply to are the same
/// answer as an id that does not exist.</para>
/// <para><b>Nothing is written by a look.</b> <c>preview</c> and <c>progress</c> read; <c>apply</c> writes only with <c>dryRun</c> false AND
/// <c>confirmed</c> true, and through the sender switch of each property; <c>pilot</c> writes the pilot group; <c>activate</c> goes through the
/// single activation service, which verifies the prerequisites again. Every answer is private and never cached.</para>
/// <para>Behind <c>Features:OctorateOnboarding</c>: 404 while it is off, before authentication.</para>
/// </remarks>
[ApiController]
[Route("api/onboarding/bulk")]
[FeatureGate(FeatureFlags.OctorateOnboarding)]
[Authorize(Policy = CasazenPolicies.OrgBillingAdmin)]
[PrivateAnswer]
public sealed class OnboardingBulkController(
    IOnboardingBulkService bulk,
    IOrgContextResolver orgContextResolver,
    IHostScopeResolver hostScopeResolver) : ControllerBase
{
    /// <summary>The total of a selection and a sample of 20 of its properties. Writes nothing.</summary>
    /// <response code="200">The total, the sample and the ids asked that are not properties of the selection.</response>
    /// <response code="422"><c>onboarding_selection_invalid</c>, <c>onboarding_property_ids_empty</c>, <c>onboarding_property_ids_too_many</c>.</response>
    [HttpPost("preview")]
    [ProducesResponseType(typeof(BulkPreviewDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<BulkPreviewDto>> Preview([FromBody] BulkSelectionDto? body, CancellationToken cancellationToken)
    {
        var scope = await ScopeAsync(cancellationToken);
        if (scope is null)
            return this.ApiProblem(StatusCodes.Status403Forbidden, ProblemCodes.Forbidden, "Forbidden");

        return Ok((await bulk.PreviewAsync(scope, body.ToModel(), cancellationToken)).ToDto());
    }

    /// <summary>
    /// Applies the choice (the sender of the Alloggiati Web and ISTAT communications) to the selection, one property at a time through the sender
    /// switch. Without <c>confirmed</c> or with <c>dryRun</c> nothing is written and each property is <c>wouldChange</c>, <c>unchanged</c> or
    /// <c>refused</c> with the rule; confirmed, <c>applied</c>, <c>unchanged</c> or <c>refused</c>. The consent to the delegation is never given in bulk.
    /// </summary>
    /// <response code="200">One item per property.</response>
    /// <response code="422"><c>onboarding_selection_invalid</c>, <c>onboarding_selection_too_large</c>, <c>onboarding_choice_invalid</c>, <c>onboarding_property_ids_empty</c>, <c>onboarding_property_ids_too_many</c>.</response>
    [HttpPost("apply")]
    [ProducesResponseType(typeof(BulkApplyResultDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<BulkApplyResultDto>> Apply([FromBody] BulkApplyRequestDto? body, CancellationToken cancellationToken)
    {
        var userId = User.GetUserId();
        var scope = await ScopeAsync(cancellationToken);
        if (scope is null || string.IsNullOrWhiteSpace(userId))
            return this.ApiProblem(StatusCodes.Status403Forbidden, ProblemCodes.Forbidden, "Forbidden");

        var result = await bulk.ApplyAsync(
            scope,
            userId,
            body?.Selection.ToModel() ?? new BulkSelection(string.Empty),
            body?.Choice.ToModel() ?? new BulkChoice(null),
            body?.DryRun ?? false,
            body?.Confirmed ?? false,
            cancellationToken);
        return Ok(result.ToDto());
    }

    /// <summary>«142 of 250 ready»: the ready properties, the four counts of each flow and the pilot group. Writes nothing.</summary>
    /// <response code="200">The progress.</response>
    [HttpGet("progress")]
    [ProducesResponseType(typeof(BulkProgressDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<BulkProgressDto>> GetProgress(CancellationToken cancellationToken)
    {
        var scope = await ScopeAsync(cancellationToken);
        if (scope is null)
            return this.ApiProblem(StatusCodes.Status403Forbidden, ProblemCodes.Forbidden, "Forbidden");

        return Ok((await bulk.GetProgressAsync(scope, cancellationToken)).ToDto());
    }

    /// <summary>The pilot group the caller reaches, with how far each flow has got on it.</summary>
    /// <response code="200">The pilot group.</response>
    [HttpGet("pilot")]
    [ProducesResponseType(typeof(BulkPilotDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<BulkPilotDto>> GetPilot(CancellationToken cancellationToken)
    {
        var scope = await ScopeAsync(cancellationToken);
        if (scope is null)
            return this.ApiProblem(StatusCodes.Status403Forbidden, ProblemCodes.Forbidden, "Forbidden");

        return Ok((await bulk.GetPilotAsync(scope, cancellationToken)).ToDto());
    }

    /// <summary>
    /// Replaces the pilot group: 1 to <c>Onboarding:PilotGroupMaxSize</c> (10) distinct properties of the caller; fewer than 5 is accepted and
    /// reported as <c>belowRecommended</c>.
    /// </summary>
    /// <response code="200">The pilot group after the change.</response>
    /// <response code="404"><c>onboarding_property_not_found</c>: an id is not a property of the caller that the flows apply to; nothing is written.</response>
    /// <response code="422"><c>onboarding_property_ids_empty</c>, <c>pilot_group_too_large</c>.</response>
    [HttpPut("pilot")]
    [ProducesResponseType(typeof(BulkPilotDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<BulkPilotDto>> SetPilot([FromBody] BulkPilotRequestDto? body, CancellationToken cancellationToken)
    {
        var userId = User.GetUserId();
        var scope = await ScopeAsync(cancellationToken);
        if (scope is null || string.IsNullOrWhiteSpace(userId))
            return this.ApiProblem(StatusCodes.Status403Forbidden, ProblemCodes.Forbidden, "Forbidden");

        return Ok((await bulk.SetPilotAsync(scope, userId, body?.PropertyIds ?? [], cancellationToken)).ToDto());
    }

    /// <summary>
    /// Activates flows on a cohort through the activation service: <c>pilot</c> the pilot group, <c>rest</c> every other property whose
    /// prerequisites are met (the others are reported <c>prerequisitesNotMet</c>). <c>rest</c> waits for the pilot group when the caller has more
    /// properties than the pilot maximum.
    /// </summary>
    /// <response code="200">For each flow asked, one outcome per property.</response>
    /// <response code="400"><c>onboarding_flow_unknown</c>.</response>
    /// <response code="409"><c>pilot_group_not_active</c>: the pilot group is not set or not active in every flow asked.</response>
    /// <response code="422"><c>onboarding_cohort_invalid</c>, <c>onboarding_flows_empty</c>, <c>onboarding_pilot_group_empty</c>.</response>
    [HttpPost("activate")]
    [ProducesResponseType(typeof(BulkActivateResultDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<BulkActivateResultDto>> Activate([FromBody] BulkActivateRequestDto? body, CancellationToken cancellationToken)
    {
        var flows = new List<OnboardingFlow>();
        foreach (var name in body?.Flows ?? [])
        {
            if (!EnumNames.TryParseDefined<OnboardingFlow>(name, out var flow))
                return this.ApiProblem(StatusCodes.Status400BadRequest, "onboarding_flow_unknown", "OnboardingFlowUnknown");

            flows.Add(flow);
        }

        var userId = User.GetUserId();
        var scope = await ScopeAsync(cancellationToken);
        if (scope is null || string.IsNullOrWhiteSpace(userId))
            return this.ApiProblem(StatusCodes.Status403Forbidden, ProblemCodes.Forbidden, "Forbidden");

        var result = await bulk.ActivateAsync(scope, userId, body?.Cohort ?? string.Empty, flows, cancellationToken);
        return Ok(result.ToDto());
    }

    private async Task<HostScope?> ScopeAsync(CancellationToken cancellationToken)
    {
        var orgId = await orgContextResolver.GetOrProvisionOrgIdAsync(cancellationToken);
        return orgId is null ? null : await hostScopeResolver.ResolveHostScopeAsync(User, orgId.Value, cancellationToken);
    }
}
