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
/// The guided steps of the Octorate panel (NF-107, ONB-04, E10/F01/US03/T01): the connection of the police and of ISTAT and the access to a
/// channel, which Octorate lets a person do only from its own panel. The host is guided there and back, the result is verified where CasaZen can
/// read it (D17, D31) and otherwise confirmed by the host, and a step left half way raises a "To do". Other wizards reuse the same endpoints.
/// </summary>
/// <remarks>
/// <para><b>Policy.</b> <see cref="CasazenPolicies.OrgBillingAdmin"/>: an org-level decision. Every step is bound to the caller's org and to what
/// the caller reaches (<c>HostScope</c>, AM-03); a step it does not reach is a 404.</para>
/// <para><b>Answers.</b> Codes, ids and instants only; private and never cached. The panel address carries no org, property or person; the callback
/// state is single-use and expiring and is never stored or logged in clear.</para>
/// <para>Behind <c>Features:OctorateOnboarding</c>: 404 while it is off, before authentication.</para>
/// </remarks>
[ApiController]
[Route("api/onboarding/panel-steps")]
[FeatureGate(FeatureFlags.OctorateOnboarding)]
[Authorize(Policy = CasazenPolicies.OrgBillingAdmin)]
public sealed class OnboardingPanelStepsController(
    IOnboardingPanelStepService steps,
    IOrgContextResolver orgContextResolver,
    IHostScopeResolver hostScopeResolver) : ControllerBase
{
    /// <summary>
    /// Starts the step (or starts it again): the step, the hand-over and the point to resume are stored before the address is returned, then the
    /// "To do" is raised. Idempotent.
    /// </summary>
    /// <response code="200">The stored state, <c>panelUrl</c> and, for <c>redirectCallback</c>, <c>callbackUrl</c>.</response>
    /// <response code="404"><c>onboarding_property_not_found</c>.</response>
    /// <response code="409"><c>octorate_panel_url_missing</c>: the address of the panel is not configured.</response>
    /// <response code="422"><c>panel_step_kind_invalid</c>, <c>panel_step_channel_invalid</c>, <c>panel_step_resume_invalid</c>.</response>
    [HttpPost]
    [PrivateAnswer]
    [ProducesResponseType(typeof(StartedPanelStepDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<StartedPanelStepDto>> Start([FromBody] StartPanelStepDto? body, CancellationToken cancellationToken)
    {
        var caller = await CallerAsync(cancellationToken);
        if (caller is null)
            return this.ApiProblem(StatusCodes.Status403Forbidden, ProblemCodes.Forbidden, "Forbidden");

        var request = new PanelStepStartRequest(
            body?.Kind ?? default,
            body?.PropertyId,
            body?.ChannelCode,
            body?.ResumeWizardType,
            body?.ResumeDraftEntityId,
            body?.ResumeStepCode);
        return Ok((await steps.StartAsync(caller.Scope, caller.UserId, request, cancellationToken)).ToDto());
    }

    /// <summary>The steps of the org the caller reaches, the most recently started first.</summary>
    /// <response code="200">The stored state of each step.</response>
    [HttpGet]
    [PrivateAnswer]
    [ProducesResponseType(typeof(List<PanelStepDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<List<PanelStepDto>>> List(CancellationToken cancellationToken)
    {
        var caller = await CallerAsync(cancellationToken);
        if (caller is null)
            return this.ApiProblem(StatusCodes.Status403Forbidden, ProblemCodes.Forbidden, "Forbidden");

        return Ok((await steps.ListAsync(caller.Scope, cancellationToken)).Select(step => step.ToDto()).ToList());
    }

    /// <summary>
    /// The stored state of the step plus <c>nextCheckAt</c>, calculated by the server. When a check is due it runs now (under a lock of the step, with
    /// a wait that doubles up to ten minutes): a page that polls never burns the quota of Octorate.
    /// </summary>
    /// <response code="200">The stored state.</response>
    /// <response code="404"><c>panel_step_not_found</c>.</response>
    [HttpGet("{id:guid}/status")]
    [PrivateAnswer]
    [ProducesResponseType(typeof(PanelStepDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<PanelStepDto>> Status(Guid id, CancellationToken cancellationToken)
    {
        var caller = await CallerAsync(cancellationToken);
        if (caller is null)
            return this.ApiProblem(StatusCodes.Status403Forbidden, ProblemCodes.Forbidden, "Forbidden");

        return Ok((await steps.GetStatusAsync(caller.Scope, id, cancellationToken)).ToDto());
    }

    /// <summary>The explicit trigger of a check (the window of the panel was closed, the tab got the focus again): held to a minimum gap from the previous check.</summary>
    /// <response code="200">The stored state, after the check if it ran.</response>
    /// <response code="404"><c>panel_step_not_found</c>.</response>
    [HttpPost("{id:guid}/verify")]
    [PrivateAnswer]
    [ProducesResponseType(typeof(PanelStepDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<PanelStepDto>> Verify(Guid id, CancellationToken cancellationToken)
    {
        var caller = await CallerAsync(cancellationToken);
        if (caller is null)
            return this.ApiProblem(StatusCodes.Status403Forbidden, ProblemCodes.Forbidden, "Forbidden");

        return Ok((await steps.VerifyAsync(caller.Scope, id, cancellationToken)).ToDto());
    }

    /// <summary>
    /// The host says the step is done: allowed only after a real check found that CasaZen cannot tell. The manual confirmation is the technical
    /// fallback, never the normal path.
    /// </summary>
    /// <response code="200">The step, now <c>confirmed</c>.</response>
    /// <response code="404"><c>panel_step_not_found</c>.</response>
    /// <response code="409"><c>panel_step_verification_required</c> (no check yet), <c>panel_step_not_valid</c> (the last check found it not valid), <c>panel_step_assist_open</c> (support is handling it).</response>
    [HttpPost("{id:guid}/confirm")]
    [PrivateAnswer]
    [ProducesResponseType(typeof(PanelStepDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<PanelStepDto>> Confirm(Guid id, CancellationToken cancellationToken)
    {
        var caller = await CallerAsync(cancellationToken);
        if (caller is null)
            return this.ApiProblem(StatusCodes.Status403Forbidden, ProblemCodes.Forbidden, "Forbidden");

        return Ok((await steps.ConfirmAsync(caller.Scope, caller.UserId, id, cancellationToken)).ToDto());
    }

    /// <summary>
    /// The callback of a panel that sends the person back (only a step in <c>redirectCallback</c>): the <c>state</c> is consumed, valid once and
    /// before its expiry, and the step is verified. This form carries the state in the query.
    /// </summary>
    /// <response code="200">The step, after the check.</response>
    /// <response code="404"><c>panel_step_not_found</c>.</response>
    /// <response code="409"><c>panel_step_callback_unsupported</c>: the step is a managed window.</response>
    /// <response code="422"><c>panel_step_state_invalid</c>: unknown, used, expired or of another step.</response>
    [HttpGet("{id:guid}/callback")]
    [PrivateAnswer]
    [ProducesResponseType(typeof(PanelStepDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public Task<ActionResult<PanelStepDto>> Callback(Guid id, [FromQuery] string? state, CancellationToken cancellationToken) =>
        CompleteCallbackAsync(id, state, cancellationToken);

    /// <summary>The same callback with the <c>state</c> in the body, so that it stays out of the address.</summary>
    [HttpPost("{id:guid}/callback")]
    [PrivateAnswer]
    [ProducesResponseType(typeof(PanelStepDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public Task<ActionResult<PanelStepDto>> CallbackWithBody(Guid id, [FromBody] PanelStepCallbackDto? body, CancellationToken cancellationToken) =>
        CompleteCallbackAsync(id, body?.State, cancellationToken);

    private async Task<ActionResult<PanelStepDto>> CompleteCallbackAsync(Guid id, string? state, CancellationToken cancellationToken)
    {
        var caller = await CallerAsync(cancellationToken);
        if (caller is null)
            return this.ApiProblem(StatusCodes.Status403Forbidden, ProblemCodes.Forbidden, "Forbidden");

        return Ok((await steps.CompleteCallbackAsync(caller.Scope, id, state, cancellationToken)).ToDto());
    }

    private async Task<Caller?> CallerAsync(CancellationToken cancellationToken)
    {
        var userId = User.GetUserId();
        var orgId = await orgContextResolver.GetOrProvisionOrgIdAsync(cancellationToken);
        if (orgId is null || string.IsNullOrWhiteSpace(userId))
            return null;

        var scope = await hostScopeResolver.ResolveHostScopeAsync(User, orgId.Value, cancellationToken);
        return scope is null ? null : new Caller(scope, userId);
    }

    private sealed record Caller(HostScope Scope, string UserId);
}
