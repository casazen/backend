using Casazen.Core.Authorization;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Features;
using Casazen.Core.Models;
using Casazen.Core.Services;
using Casazen.Core.Validation;
using Casazen.Web.Authorization;
using Casazen.Web.DTOs;
using Casazen.Web.DTOs.Onboarding;
using Casazen.Web.Infrastructure;
using Casazen.Web.Mapping;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Casazen.Web.Controllers;

/// <summary>
/// The status of the six flows (listings, bookings, check-in and compliance, messages, documents, reports) on the properties of the caller
/// (NF-106, ONB-03, E10/F01/US01/T02): for each property, active, to complete (with the reason and the action that fixes it) or not
/// available (with why). Nothing here writes: the status is derived on every read.
/// </summary>
/// <remarks>
/// <para><b>Policy.</b> <see cref="CasazenPolicies.PropertyRead"/>: the short-stay side of the properties, as the other host lists. The
/// properties are bound in SQL to the caller's org and to what it reaches (<see cref="HostScope"/>, AM-03, resolved from the membership in
/// the database, never from the request), so a collaborator «Solo alcuni» sees only the properties it was given and nobody sees a property
/// of another org.</para>
/// <para><b>Answers.</b> Codes only (state, reason, action, prerequisite keys): the client translates them. The answers are private and
/// never cached (<c>Cache-Control: no-store</c>): they name the properties of the caller.</para>
/// <para>Behind <c>Features:OctorateOnboarding</c>: 404 while it is off, before authentication.</para>
/// </remarks>
[ApiController]
[Route("api/onboarding/flows")]
[FeatureGate(FeatureFlags.OctorateOnboarding)]
[Authorize(Policy = CasazenPolicies.PropertyRead)]
public sealed class OnboardingFlowsController(
    IOnboardingFlowStatusService flowStatus,
    IOrgContextResolver orgContextResolver,
    IHostScopeResolver hostScopeResolver) : ControllerBase
{
    /// <summary>
    /// The properties the caller reaches with their status for one flow, a page at a time, by name. <c>flow</c> is the name of an
    /// <see cref="OnboardingFlow"/> (<c>Listings</c>, <c>Bookings</c>, <c>CheckInCompliance</c>, <c>Messages</c>, <c>Documents</c>,
    /// <c>Reports</c>). <c>state</c> keeps the properties in one state (<c>active</c>, <c>toComplete</c>, <c>notAvailable</c>); <c>search</c>
    /// finds a text in the name or the city; <c>page</c> starts from 1 and <c>pageSize</c> is 50 when missing and at most 100.
    /// </summary>
    /// <response code="200"><c>{ items, totalCount, page, pageSize }</c>; an item is <c>{ propertyId, name, city, flow, state, reason, action, prerequisites }</c>.</response>
    /// <response code="400"><c>onboarding_flow_unknown</c>, <c>onboarding_flow_state_unknown</c>.</response>
    /// <response code="403">The caller has no org, or reaches nothing in it.</response>
    [HttpGet("{flow}/properties")]
    [PrivateAnswer]
    [ProducesResponseType(typeof(PagedResultDto<FlowPropertyStatusDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<PagedResultDto<FlowPropertyStatusDto>>> ListProperties(
        string flow,
        [FromQuery] string? state,
        [FromQuery] string? search,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = FlowPropertyPage.DefaultPageSize,
        CancellationToken cancellationToken = default)
    {
        if (!EnumNames.TryParseDefined<OnboardingFlow>(flow, out var parsedFlow))
            return this.ApiProblem(StatusCodes.Status400BadRequest, "onboarding_flow_unknown", "OnboardingFlowUnknown");

        string? parsedState = null;
        if (!string.IsNullOrWhiteSpace(state))
        {
            if (!OnboardingFlowStates.TryParse(state, out var known))
                return this.ApiProblem(StatusCodes.Status400BadRequest, "onboarding_flow_state_unknown", "OnboardingFlowStateUnknown");

            parsedState = known;
        }

        var orgId = await orgContextResolver.GetOrProvisionOrgIdAsync(cancellationToken);
        var scope = orgId is null ? null : await hostScopeResolver.ResolveHostScopeAsync(User, orgId.Value, cancellationToken);
        if (scope is null)
            return this.ApiProblem(StatusCodes.Status403Forbidden, ProblemCodes.Forbidden, "Forbidden");

        var result = await flowStatus.GetPropertiesAsync(scope, parsedFlow, parsedState, search, page, pageSize, cancellationToken);
        return Ok(result.ToDto());
    }
}
