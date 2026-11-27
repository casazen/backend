using Casazen.Core.Authorization;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Features;
using Casazen.Core.Options;
using Casazen.Core.Services;
using Casazen.Core.Validation;
using Casazen.Web.Authorization;
using Casazen.Web.DTOs.Onboarding;
using Casazen.Web.Infrastructure;
using Casazen.Web.Mapping;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace Casazen.Web.Controllers;

/// <summary>
/// The activation of the flows on the properties and the final test (NF-104, NF-106, NF-108, ONB-01 part 11, ONB-05, E10/F01/US01/T04): an org
/// administrator activates or deactivates a flow on properties, and the server verifies the prerequisites of each one again; the final test passes
/// a recently imported booking through every flow without any effect.
/// </summary>
/// <remarks>
/// <para><b>Policy.</b> <see cref="CasazenPolicies.OrgBillingAdmin"/>: an org-level decision. The properties are bound in SQL to the caller's org
/// (an explicit <c>OrgId</c> predicate) and to what it reaches (<c>HostScope</c>, AM-03): a property of another org is <c>notFound</c>.</para>
/// <para>Behind <c>Features:OctorateOnboarding</c>: 404 while it is off, before authentication.</para>
/// </remarks>
[ApiController]
[Route("api/onboarding")]
[FeatureGate(FeatureFlags.OctorateOnboarding)]
[Authorize(Policy = CasazenPolicies.OrgBillingAdmin)]
public sealed class OnboardingActivationController(
    IOnboardingFlowActivationService activation,
    IOnboardingFlowTestRunService testRun,
    IOrgContextResolver orgContextResolver,
    IHostScopeResolver hostScopeResolver,
    IOptions<OnboardingOptions> options) : ControllerBase
{
    /// <summary>
    /// Activates <c>flow</c> (a member name of <c>OnboardingFlow</c>) on each property of the body whose prerequisites the server verifies now.
    /// </summary>
    /// <response code="200">One outcome per property: <c>activated</c>, <c>alreadyActive</c>, <c>prerequisitesNotMet</c>, <c>notAvailable</c>, <c>notFound</c>.</response>
    /// <response code="400"><c>onboarding_flow_unknown</c>.</response>
    /// <response code="422"><c>onboarding_property_ids_empty</c>, <c>onboarding_property_ids_too_many</c>.</response>
    [HttpPost("flows/{flow}/activate")]
    [ProducesResponseType(typeof(FlowActivationResultDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<FlowActivationResultDto>> Activate(
        string flow,
        [FromBody] FlowActivationRequestDto? body,
        CancellationToken cancellationToken)
    {
        return await ChangeAsync(flow, body, activate: true, cancellationToken);
    }

    /// <summary>Deactivates <c>flow</c> on each property of the body where it stands activated (the row stays, with when and who).</summary>
    /// <response code="200">One outcome per property: <c>deactivated</c>, <c>alreadyInactive</c>, <c>notFound</c>.</response>
    /// <response code="400"><c>onboarding_flow_unknown</c>.</response>
    /// <response code="422"><c>onboarding_property_ids_empty</c>, <c>onboarding_property_ids_too_many</c>.</response>
    [HttpPost("flows/{flow}/deactivate")]
    [ProducesResponseType(typeof(FlowActivationResultDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<FlowActivationResultDto>> Deactivate(
        string flow,
        [FromBody] FlowActivationRequestDto? body,
        CancellationToken cancellationToken)
    {
        return await ChangeAsync(flow, body, activate: false, cancellationToken);
    }

    /// <summary>
    /// The final test: the booking of the body (or the most recent booking of the property imported from Octorate) passes through every flow without
    /// any effect: nothing is written, no event is recorded, nothing is sent.
    /// </summary>
    /// <response code="200">One check per flow: <c>pass</c>, <c>fail</c> or <c>skipped</c> with reason codes.</response>
    /// <response code="404"><c>onboarding_property_not_found</c>, <c>test_booking_not_found</c>.</response>
    [HttpPost("test-run")]
    [PrivateAnswer]
    [ProducesResponseType(typeof(FlowTestRunDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<FlowTestRunDto>> TestRun([FromBody] FlowTestRunRequestDto body, CancellationToken cancellationToken)
    {
        var scope = await ScopeAsync(cancellationToken);
        if (scope is null)
            return this.ApiProblem(StatusCodes.Status403Forbidden, ProblemCodes.Forbidden, "Forbidden");

        return Ok((await testRun.RunAsync(scope, body.PropertyId, body.BookingId, cancellationToken)).ToDto());
    }

    private async Task<ActionResult<FlowActivationResultDto>> ChangeAsync(
        string flow,
        FlowActivationRequestDto? body,
        bool activate,
        CancellationToken cancellationToken)
    {
        if (!EnumNames.TryParseDefined<OnboardingFlow>(flow, out var parsedFlow))
            return this.ApiProblem(StatusCodes.Status400BadRequest, "onboarding_flow_unknown", "OnboardingFlowUnknown");

        var ids = (body?.PropertyIds ?? []).Distinct().ToList();
        if (ids.Count == 0)
        {
            return this.ApiProblem(
                StatusCodes.Status422UnprocessableEntity, OnboardingActivationCodes.PropertyIdsEmpty, "OnboardingPropertyIdsEmpty");
        }

        var cap = options.Value.EffectiveMaxPropertiesPerCall;
        if (ids.Count > cap)
        {
            return this.ApiProblem(
                StatusCodes.Status422UnprocessableEntity, OnboardingActivationCodes.PropertyIdsTooMany, "OnboardingPropertyIdsTooMany", cap);
        }

        var userId = User.GetUserId();
        var scope = await ScopeAsync(cancellationToken);
        if (scope is null || string.IsNullOrWhiteSpace(userId))
            return this.ApiProblem(StatusCodes.Status403Forbidden, ProblemCodes.Forbidden, "Forbidden");

        var outcomes = activate
            ? await activation.ActivateAsync(scope, userId, parsedFlow, ids, cancellationToken)
            : await activation.DeactivateAsync(scope, userId, parsedFlow, ids, cancellationToken);
        return Ok(outcomes.ToDto(parsedFlow));
    }

    private async Task<HostScope?> ScopeAsync(CancellationToken cancellationToken)
    {
        var orgId = await orgContextResolver.GetOrProvisionOrgIdAsync(cancellationToken);
        return orgId is null ? null : await hostScopeResolver.ResolveHostScopeAsync(User, orgId.Value, cancellationToken);
    }
}
