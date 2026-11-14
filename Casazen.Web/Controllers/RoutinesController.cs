using System.Globalization;
using System.Text.Json;
using Casazen.Core.Authorization;
using Casazen.Core.Features;
using Casazen.Core.Models;
using Casazen.Core.Multitenancy;
using Casazen.Core.Services;
using Casazen.Web.Authorization;
using Casazen.Web.DTOs.Routines;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Casazen.Web.Controllers;

/// <summary>
/// The automation routines of the host (E01/F04/US01/T03, NF-21, DEB-06, ADR-022): the list, one routine, the changes (create, change, delete, switch
/// on and off, switch test mode), the log of a routine, the simulation of an event and the catalog of the actions. The endpoints the routines page calls.
/// </summary>
/// <remarks>
/// <para><b>Behind <c>Features:Routines</c></b>: 404 while it is off, before authentication.</para>
/// <para><b>Policies.</b> The class carries <see cref="CasazenPolicies.PropertyRead"/>; the actions that change something, and the simulation (which reads
/// a booking of the org), add <see cref="CasazenPolicies.PropertyWrite"/> <b>and</b> need a scope over the whole org: a routine acts on every property
/// of the org, so a collaborator limited to some properties gets 403 <c>routines_need_org_scope</c> on them. A routine belongs to no property, so for the
/// same caller the reads show none of them (an empty list, 404 for one routine and for its log), as an item of the org does in every restricted scope.</para>
/// <para><b>Errors.</b> 404 <c>routine_not_found</c> (also for a routine of another org); 409 <c>routine_limit_reached</c>; 422
/// <c>routine_name_invalid</c>, <c>routine_event_type_invalid</c>, <c>routine_event_not_available</c>, <c>routine_event_config_invalid</c>,
/// <c>routine_condition_invalid</c> (with <c>errors</c>), <c>routine_actions_invalid</c>, <c>routine_action_config_invalid</c>,
/// <c>routine_action_not_available</c> (an active routine outside test mode holds an action that cannot run: <c>actions</c> names them),
/// <c>routine_payload_invalid</c>. The answers are private and never cached; no name, text or condition is ever logged.</para>
/// </remarks>
[ApiController]
[Route("api/routines")]
[FeatureGate(FeatureFlags.Routines)]
[Authorize(Policy = CasazenPolicies.PropertyRead)]
[PrivateAnswer]
public sealed class RoutinesController(
    IRoutineManagementService routines,
    IRoutineService engine,
    IFeatureFlags featureFlags,
    ITenantContext tenantContext,
    IHostScopeResolver hostScopeResolver,
    ILogger<RoutinesController> logger) : ControllerBase
{
    /// <summary>The largest body of a request that writes: ten actions of 4 KB and a condition of 16 KB fit with room to spare.</summary>
    private const int MaxBodyBytes = 131072;

    /// <summary>The routines of the org, by name: all of them for a caller who reaches the whole org, none for one limited to some properties.</summary>
    [HttpGet]
    [ProducesResponseType(typeof(IReadOnlyList<RoutineDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<IReadOnlyList<RoutineDto>>> List(CancellationToken cancellationToken)
    {
        var scope = await ResolveScopeAsync(cancellationToken);
        if (scope.Problem is not null)
            return scope.Problem;

        if (!scope.Value!.IsOrgWide)
            return Ok(Array.Empty<RoutineDto>());

        var all = await routines.ListAsync(scope.Value.OrgId, cancellationToken);
        return Ok(all.Select(RoutineDto.From).ToList());
    }

    /// <summary>The actions a routine can hold, each with whether it can run today and, when it cannot, <c>routine_action_not_available</c>. The same list the engine and the activation read.</summary>
    [HttpGet("actions")]
    [ProducesResponseType(typeof(IReadOnlyList<RoutineActionCatalogItemDto>), StatusCodes.Status200OK)]
    public ActionResult<IReadOnlyList<RoutineActionCatalogItemDto>> Actions() =>
        Ok(RoutineActionCatalog.All(featureFlags).Select(RoutineActionCatalogItemDto.From).ToList());

    /// <summary>One routine of the org.</summary>
    [HttpGet("{id:guid}")]
    [ProducesResponseType(typeof(RoutineDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<RoutineDto>> Get(Guid id, CancellationToken cancellationToken)
    {
        var scope = await ResolveScopeAsync(cancellationToken);
        if (scope.Problem is not null)
            return scope.Problem;

        if (!scope.Value!.IsOrgWide)
            return RoutineNotFound();

        return Ok(RoutineDto.From(await routines.GetAsync(scope.Value.OrgId, id, cancellationToken)));
    }

    /// <summary>
    /// Creates a routine: off and not in test mode unless the body says so. 422 for what is not valid (<c>routine_name_invalid</c>…), 409
    /// <c>routine_limit_reached</c> at 50 routines.
    /// </summary>
    [HttpPost]
    [Authorize(Policy = CasazenPolicies.PropertyWrite)]
    [RequestSizeLimit(MaxBodyBytes)]
    [ProducesResponseType(typeof(RoutineDto), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<RoutineDto>> Create([FromBody] CreateRoutineDto dto, CancellationToken cancellationToken)
    {
        var scope = await ResolveWriterScopeAsync(cancellationToken);
        if (scope.Problem is not null)
            return scope.Problem;

        var routine = await routines.CreateAsync(scope.Value!.OrgId, dto.ToCommand(), cancellationToken);

        logger.LogInformation("User {UserId} created routine {RoutineId}", User.GetUserId(), routine.Id);
        return CreatedAtAction(nameof(Get), new { id = routine.Id }, RoutineDto.From(routine));
    }

    /// <summary>Changes a routine: what the body leaves out stays as it is, and the whole routine that results is validated.</summary>
    [HttpPut("{id:guid}")]
    [Authorize(Policy = CasazenPolicies.PropertyWrite)]
    [RequestSizeLimit(MaxBodyBytes)]
    [ProducesResponseType(typeof(RoutineDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<RoutineDto>> Update(Guid id, [FromBody] UpdateRoutineDto dto, CancellationToken cancellationToken)
    {
        var scope = await ResolveWriterScopeAsync(cancellationToken);
        if (scope.Problem is not null)
            return scope.Problem;

        var routine = await routines.UpdateAsync(scope.Value!.OrgId, id, dto.ToCommand(), cancellationToken);

        logger.LogInformation("User {UserId} changed routine {RoutineId}", User.GetUserId(), id);
        return Ok(RoutineDto.From(routine));
    }

    /// <summary>Deletes a routine and its log.</summary>
    [HttpDelete("{id:guid}")]
    [Authorize(Policy = CasazenPolicies.PropertyWrite)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken cancellationToken)
    {
        var scope = await ResolveWriterScopeAsync(cancellationToken);
        if (scope.Problem is not null)
            return scope.Problem;

        await routines.DeleteAsync(scope.Value!.OrgId, id, cancellationToken);

        logger.LogInformation("User {UserId} deleted routine {RoutineId}", User.GetUserId(), id);
        return NoContent();
    }

    /// <summary>Switches a routine on or off. On, outside test mode, 422 <c>routine_action_not_available</c> when an action cannot run yet.</summary>
    [HttpPut("{id:guid}/active")]
    [Authorize(Policy = CasazenPolicies.PropertyWrite)]
    [ProducesResponseType(typeof(RoutineDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<RoutineDto>> SetActive(Guid id, [FromBody] SetRoutineActiveDto dto, CancellationToken cancellationToken)
    {
        var scope = await ResolveWriterScopeAsync(cancellationToken);
        if (scope.Problem is not null)
            return scope.Problem;

        var routine = await routines.SetActiveAsync(scope.Value!.OrgId, id, dto.Active!.Value, cancellationToken);

        logger.LogInformation("User {UserId} switched routine {RoutineId} {State}", User.GetUserId(), id, routine.Active ? "on" : "off");
        return Ok(RoutineDto.From(routine));
    }

    /// <summary>Switches test mode on or off (the routine then simulates its actions, or runs them). Off, on an active routine, the same 422 as the activation.</summary>
    [HttpPut("{id:guid}/test-mode")]
    [Authorize(Policy = CasazenPolicies.PropertyWrite)]
    [ProducesResponseType(typeof(RoutineDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<RoutineDto>> SetTestMode(Guid id, [FromBody] SetRoutineTestModeDto dto, CancellationToken cancellationToken)
    {
        var scope = await ResolveWriterScopeAsync(cancellationToken);
        if (scope.Problem is not null)
            return scope.Problem;

        var routine = await routines.SetTestModeAsync(scope.Value!.OrgId, id, dto.TestMode!.Value, cancellationToken);

        logger.LogInformation("User {UserId} switched test mode of routine {RoutineId} {State}", User.GetUserId(), id, routine.TestMode ? "on" : "off");
        return Ok(RoutineDto.From(routine));
    }

    /// <summary>
    /// The log of a routine, the newest execution first, a page at a time (<c>page</c> from 1, <c>pageSize</c> 1 to 100, 50 when left out); the total is
    /// in the header <c>X-Total-Count</c>. The saved payload of an execution that waits is not shown.
    /// </summary>
    [HttpGet("{id:guid}/executions")]
    [ProducesResponseType(typeof(IReadOnlyList<RoutineExecutionDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<IReadOnlyList<RoutineExecutionDto>>> Executions(
        Guid id,
        CancellationToken cancellationToken,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = RoutineLimits.DefaultExecutionsPageSize)
    {
        var scope = await ResolveScopeAsync(cancellationToken);
        if (scope.Problem is not null)
            return scope.Problem;

        if (!scope.Value!.IsOrgWide)
            return RoutineNotFound();

        var result = await routines.ListExecutionsAsync(scope.Value.OrgId, id, page, pageSize, cancellationToken);

        Response.Headers["X-Total-Count"] = result.TotalCount.ToString(CultureInfo.InvariantCulture);
        return Ok(result.Items.Select(RoutineExecutionDto.From).ToList());
    }

    /// <summary>
    /// What the routine would do for a sample event (the body, a JSON object that says which booking it is about with <c>bookingId</c>): whether its
    /// condition is met, and for each action whether it would be stopped (a guardrail, or an action that cannot run yet). Nothing is written, scheduled or sent.
    /// </summary>
    [HttpPost("{id:guid}/simulate")]
    [Authorize(Policy = CasazenPolicies.PropertyWrite)]
    [RequestSizeLimit(MaxBodyBytes)]
    [ProducesResponseType(typeof(RoutineSimulationResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<RoutineSimulationResult>> Simulate(Guid id, [FromBody] JsonElement samplePayload, CancellationToken cancellationToken)
    {
        var scope = await ResolveWriterScopeAsync(cancellationToken);
        if (scope.Problem is not null)
            return scope.Problem;

        if (samplePayload.ValueKind != JsonValueKind.Object)
            return this.ApiProblem(StatusCodes.Status422UnprocessableEntity, RoutineCodes.SimulationPayloadInvalid, "RoutinePayloadInvalid");

        return Ok(await engine.SimulateAsync(scope.Value!.OrgId, id, samplePayload, cancellationToken));
    }

    /// <summary>The caller's reach on the host data of its org: a problem (401 with no org, 403 with no reach) or the scope. Never widened.</summary>
    private async Task<(HostScope? Value, ObjectResult? Problem)> ResolveScopeAsync(CancellationToken cancellationToken)
    {
        var orgId = tenantContext.OrgId ?? Guid.Empty;
        if (orgId == Guid.Empty)
        {
            return (null, this.ApiProblem(
                StatusCodes.Status401Unauthorized,
                ProblemCodes.Unauthorized,
                "OrgContextRequired"));
        }

        var scope = await hostScopeResolver.ResolveHostScopeAsync(User, orgId, cancellationToken);
        return scope is null
            ? (null, this.ApiProblem(StatusCodes.Status403Forbidden, ProblemCodes.Forbidden, "Forbidden"))
            : (scope, null);
    }

    /// <summary>
    /// <see cref="ResolveScopeAsync"/> for what changes a routine or reads a booking for the simulation: a routine acts on every property of the org, so
    /// the caller reaches the whole org, or it is 403 <c>routines_need_org_scope</c>.
    /// </summary>
    private async Task<(HostScope? Value, ObjectResult? Problem)> ResolveWriterScopeAsync(CancellationToken cancellationToken)
    {
        var scope = await ResolveScopeAsync(cancellationToken);
        if (scope.Problem is not null || scope.Value!.IsOrgWide)
            return scope;

        return (null, this.ApiProblem(StatusCodes.Status403Forbidden, RoutineCodes.NeedsOrgScope, "RoutineNeedsOrgScope"));
    }

    private ObjectResult RoutineNotFound() =>
        this.ApiProblem(StatusCodes.Status404NotFound, RoutineCodes.NotFound, "RoutineNotFound");
}
