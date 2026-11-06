using Casazen.Core.Authorization;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Features;
using Casazen.Core.Models;
using Casazen.Core.Services;
using Casazen.Web.Authorization;
using Casazen.Web.DTOs.Onboarding;
using Casazen.Web.Infrastructure;
using Casazen.Web.Mapping;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Casazen.Web.Controllers;

/// <summary>
/// The plan of the activation of CasaZen with Octorate (NF-104, NF-10, ONB-01, UX-06, UX-10, E10/F01/US01/T03): the eleven parts with the state
/// of each, derived on every read from the stored facts, the connected path of UX-10, and the host's «Do it later» (defer), «Save and exit»
/// (exit) and resume of a part, each with its "To do".
/// </summary>
/// <remarks>
/// <para><b>Policies.</b> The reads need <see cref="CasazenPolicies.SharedPropertyRead"/> (<c>property.read</c> in either rental context), and
/// count only the properties the caller reaches (<c>HostScope</c>, AM-03, resolved from the membership in the database): a collaborator «Solo
/// alcuni» sees the plan of the properties it was given. The mutations are org-level decisions: <see cref="CasazenPolicies.OrgBillingAdmin"/>.</para>
/// <para><b>Answers.</b> Codes, counts and ids only; private and never cached. The plan page keeps its small answers in the server draft of the
/// wizard <c>OnboardingActivation</c> (<c>POST api/wizards/OnboardingActivation/draft</c>): there is no draft endpoint here.</para>
/// <para>Behind <c>Features:OctorateOnboarding</c>: 404 while it is off, before authentication.</para>
/// </remarks>
[ApiController]
[Route("api/onboarding")]
[FeatureGate(FeatureFlags.OctorateOnboarding)]
public sealed class OnboardingPlanController(
    IOnboardingPlanService plan,
    IOrgContextResolver orgContextResolver,
    IHostScopeResolver hostScopeResolver) : ControllerBase
{
    /// <summary>The plan of the caller's org over the properties it reaches.</summary>
    /// <response code="200">The eleven parts, «What you need», the path and <c>activated</c>.</response>
    /// <response code="403">The caller has no org, or reaches nothing in it.</response>
    [HttpGet("plan")]
    [Authorize(Policy = CasazenPolicies.SharedPropertyRead)]
    [PrivateAnswer]
    [ProducesResponseType(typeof(OnboardingPlanDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<OnboardingPlanDto>> GetPlan(CancellationToken cancellationToken)
    {
        var orgId = await orgContextResolver.GetOrProvisionOrgIdAsync(cancellationToken);
        var scope = orgId is null ? null : await hostScopeResolver.ResolveHostScopeAsync(User, orgId.Value, cancellationToken);
        if (scope is null)
            return this.ApiProblem(StatusCodes.Status403Forbidden, ProblemCodes.Forbidden, "Forbidden");

        return Ok((await plan.GetPlanAsync(scope.OrgId, scope, cancellationToken)).ToDto());
    }

    /// <summary>The connected path «start with CasaZen» of UX-10: its six steps mapped to the parts, which are visible, and the next one.</summary>
    /// <response code="200">The path.</response>
    /// <response code="403">The caller has no org, or reaches nothing in it.</response>
    [HttpGet("path")]
    [Authorize(Policy = CasazenPolicies.SharedPropertyRead)]
    [PrivateAnswer]
    [ProducesResponseType(typeof(OnboardingPathDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<OnboardingPathDto>> GetPath(CancellationToken cancellationToken)
    {
        var orgId = await orgContextResolver.GetOrProvisionOrgIdAsync(cancellationToken);
        var scope = orgId is null ? null : await hostScopeResolver.ResolveHostScopeAsync(User, orgId.Value, cancellationToken);
        if (scope is null)
            return this.ApiProblem(StatusCodes.Status403Forbidden, ProblemCodes.Forbidden, "Forbidden");

        return Ok((await plan.GetPathAsync(scope.OrgId, scope, cancellationToken)).ToDto());
    }

    /// <summary>
    /// «Do it later»: the part is deferred and one "To do" reminds the host at <c>remindAt</c> (UTC) or, without one, tomorrow at 09:00 in Rome.
    /// </summary>
    /// <response code="200">The part, now <c>deferred</c>, with its reminder and its "To do".</response>
    /// <response code="404"><c>onboarding_part_unknown</c>: the route names no part.</response>
    /// <response code="422"><c>onboarding_part_prerequisite</c>: another part requires this one; <c>onboarding_remind_at_invalid</c>; <c>onboarding_part_not_open</c>: the part is done or not available.</response>
    [HttpPost("parts/{key}/defer")]
    [Authorize(Policy = CasazenPolicies.OrgBillingAdmin)]
    [ProducesResponseType(typeof(OnboardingPartChangeDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<OnboardingPartChangeDto>> Defer(
        string key,
        [FromBody] DeferOnboardingPartDto? body,
        CancellationToken cancellationToken)
    {
        return await MutateAsync(
            key,
            (orgId, userId, part) => plan.DeferAsync(orgId, userId, part, body?.RemindAt, cancellationToken),
            cancellationToken);
    }

    /// <summary>«Save and exit»: the part stays open and one "To do" (tomorrow at 09:00 in Rome) keeps what remains from disappearing silently.</summary>
    /// <response code="200">The part, <c>open</c>, with its reminder and its "To do".</response>
    /// <response code="404"><c>onboarding_part_unknown</c>.</response>
    /// <response code="422"><c>onboarding_part_not_open</c>: the part is done or not available, nothing remains.</response>
    [HttpPost("parts/{key}/exit")]
    [Authorize(Policy = CasazenPolicies.OrgBillingAdmin)]
    [ProducesResponseType(typeof(OnboardingPartChangeDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<OnboardingPartChangeDto>> Exit(string key, CancellationToken cancellationToken)
    {
        return await MutateAsync(
            key,
            (orgId, userId, part) => plan.ExitAsync(orgId, userId, part, cancellationToken),
            cancellationToken);
    }

    /// <summary>The host takes the part up again: the deferral is cleared and its "To do" closed.</summary>
    /// <response code="200">The part, <c>open</c>, with no reminder and no "To do".</response>
    /// <response code="404"><c>onboarding_part_unknown</c>.</response>
    [HttpPost("parts/{key}/resume")]
    [Authorize(Policy = CasazenPolicies.OrgBillingAdmin)]
    [ProducesResponseType(typeof(OnboardingPartChangeDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<OnboardingPartChangeDto>> Resume(string key, CancellationToken cancellationToken)
    {
        return await MutateAsync(
            key,
            (orgId, userId, part) => plan.ResumeAsync(orgId, userId, part, cancellationToken),
            cancellationToken);
    }

    private async Task<ActionResult<OnboardingPartChangeDto>> MutateAsync(
        string key,
        Func<Guid, string, OnboardingPartKey, Task<OnboardingPartChange>> mutation,
        CancellationToken cancellationToken)
    {
        if (!OnboardingPartCatalog.TryParse(key, out var part))
            return this.ApiProblem(StatusCodes.Status404NotFound, OnboardingPlanCodes.PartUnknown, "OnboardingPartUnknown");

        var userId = User.GetUserId();
        var orgId = await orgContextResolver.GetOrProvisionOrgIdAsync(cancellationToken);
        if (orgId is null || string.IsNullOrWhiteSpace(userId))
            return this.ApiProblem(StatusCodes.Status403Forbidden, ProblemCodes.Forbidden, "Forbidden");

        return Ok((await mutation(orgId.Value, userId, part)).ToDto());
    }
}
