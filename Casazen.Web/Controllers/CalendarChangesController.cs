using Casazen.Core.Authorization;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;
using Casazen.Web.Authorization;
using Casazen.Web.DTOs;
using Casazen.Web.Infrastructure;
using Casazen.Web.Resources;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Localization;

namespace Casazen.Web.Controllers;

/// <summary>
/// The changes of the calendar sent to the channel manager (E08/F05/US02/T02, NF-36, NF-78): read them with their state and the stable code of each property,
/// send again one that failed or was partly sent. A change is a process
/// whose state is derived from its chunks and never says «published»: sent is «accepted by the channel manager». Behind no feature flag: a property that is not on the
/// channel manager gets a change that stays in CasaZen (<c>Applied</c>), and nothing is sent while the connector is off.
/// </summary>
/// <remarks>
/// <para>TN-3: <see cref="CasazenPolicies.PropertyRead"/> to read, <see cref="CasazenPolicies.PropertyWrite"/> to write, plus the check on every property
/// (<c>property.write</c>, the reach of the caller). A process of another org, or one that touches a property the caller does not reach, answers 404 like a missing one
/// (the read is narrowed in SQL by the caller's <see cref="HostScope"/>); a request that names a property of another org is 404 <c>property_not_found</c>, and one that
/// names a property the caller may not write is 403 for the whole request, before anything runs. Errors are ProblemDetails with stable codes (FD-05).</para>
/// </remarks>
[ApiController]
[Route("api/calendar/changes")]
[Authorize(Policy = CasazenPolicies.PropertyRead)]
public class CalendarChangesController(
    ICalendarChangeQueries queries,
    ICalendarChangeRunner runner,
    IOrgContextResolver orgContextResolver,
    IHostScopeResolver hostScopeResolver,
    IHostResourceLookup hostResources,
    IAuthorizationService authorizationService,
    IStringLocalizer<SharedResources> localizer,
    ILogger<CalendarChangesController> logger) : ControllerBase
{
    /// <summary>
    /// The changes of the caller's org it reaches, newest first, at most 50, optionally only those in a <c>state</c> (<c>Applied</c>, <c>Queued</c>, <c>Sending</c>,
    /// <c>Sent</c>, <c>Confirmed</c>, <c>PartiallySent</c>, <c>Failed</c>). Each is <see cref="CalendarChangeDto"/>.
    /// </summary>
    [HttpGet]
    [ProducesResponseType(typeof(IReadOnlyList<CalendarChangeDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<IReadOnlyList<CalendarChangeDto>>> List(
        [FromQuery] CalendarChangeState? state = null,
        [FromQuery] int? limit = null,
        CancellationToken cancellationToken = default)
    {
        if (await ScopeAsync(cancellationToken) is not { } scope)
            return this.ApiProblem(StatusCodes.Status403Forbidden, ProblemCodes.Forbidden, "Forbidden");

        var views = await queries.ListAsync(scope, new CalendarChangeListQuery(state, limit ?? CalendarChangeListQuery.MaxLimit), cancellationToken);
        return Ok(views.Select(view => CalendarChangeDto.Of(view, localizer)).ToList());
    }

    /// <summary>One change with the state of each property. 404 <c>calendar_change_not_found</c> for another org's, or one of a property the caller does not reach.</summary>
    [HttpGet("{id:guid}")]
    [ProducesResponseType(typeof(CalendarChangeDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<CalendarChangeDto>> Get(Guid id, CancellationToken cancellationToken)
    {
        if (await ScopeAsync(cancellationToken) is not { } scope)
            return this.ApiProblem(StatusCodes.Status403Forbidden, ProblemCodes.Forbidden, "Forbidden");

        var view = await queries.GetAsync(scope, id, cancellationToken);
        return view is null ? ChangeNotFound() : Ok(CalendarChangeDto.Of(view, localizer));
    }

    /// <summary>
    /// Sends again a change that failed or was partly sent: its failed chunks go back to <c>Pending</c> and the change to <c>Queued</c>, and a run is enqueued. 202 with the
    /// change. The write right is needed on every property of the change (403 otherwise, 404 for a property that is not found); 404 <c>calendar_change_not_found</c>; 409
    /// <c>calendar_change_not_retryable</c> for a change in any other state.
    /// </summary>
    [HttpPost("{id:guid}/retry")]
    [Authorize(Policy = CasazenPolicies.PropertyWrite)]
    [ProducesResponseType(typeof(CalendarChangeDto), StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<CalendarChangeDto>> Retry(Guid id, CancellationToken cancellationToken)
    {
        if (await ScopeAsync(cancellationToken) is not { } scope)
            return this.ApiProblem(StatusCodes.Status403Forbidden, ProblemCodes.Forbidden, "Forbidden");

        var view = await queries.GetAsync(scope, id, cancellationToken);
        if (view is null)
            return ChangeNotFound();

        var denied = await AuthorizeWriteAsync(view.Properties.Select(p => p.PropertyId).ToList(), cancellationToken);
        if (denied is not null)
            return denied;

        await runner.RetryAsync(id, cancellationToken);
        logger.LogInformation("Calendar change {ProcessId} asked again by user {UserId}", id, User.GetUserId());

        var updated = await queries.GetAsync(scope, id, cancellationToken);
        return updated is null ? ChangeNotFound() : Accepted(CalendarChangeDto.Of(updated, localizer));
    }

    // --- Helpers -----------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Authorizes <c>property.write</c> on every property before anything runs, as <see cref="ManualBlocksController"/> does for one: 404 <c>property_not_found</c> for a
    /// property of another org (or one that does not exist), 403 for one the caller may not write. <c>null</c> when every property is allowed.
    /// </summary>
    private async Task<ActionResult?> AuthorizeWriteAsync(IReadOnlyList<Guid> propertyIds, CancellationToken cancellationToken)
    {
        foreach (var propertyId in propertyIds.Distinct())
        {
            var property = await hostResources.ForPropertyAsync(propertyId, cancellationToken);
            if (property is null)
            {
                return this.ApiProblem(
                    StatusCodes.Status404NotFound,
                    ManualBlockErrorCodes.PropertyNotFound,
                    ManualBlockErrorCodes.PropertyNotFoundMessageKey);
            }

            if (!await authorizationService.IsAuthorizedAsync(User, property, PropertyOperations.Write))
            {
                logger.LogWarning(
                    "User {UserId} denied {Permission} on the calendar changes of property {PropertyId}",
                    User.GetUserId(), PropertyOperations.Write.PermissionKey, propertyId);
                return Forbid();
            }
        }

        return null;
    }

    private async Task<HostScope?> ScopeAsync(CancellationToken cancellationToken)
    {
        var orgId = await orgContextResolver.GetOrProvisionOrgIdAsync(cancellationToken);
        return orgId is null ? null : await hostScopeResolver.ResolveHostScopeAsync(User, orgId.Value, cancellationToken);
    }

    private ObjectResult ChangeNotFound() =>
        this.ApiProblem(StatusCodes.Status404NotFound, CalendarPushErrorCodes.NotFound, CalendarPushErrorCodes.NotFoundMessageKey);
}
