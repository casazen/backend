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
/// The changes of the calendar sent to the channel manager (E08/F05/US02/T02, T03, NF-36, NF-78): read them with their state and the stable code of each property,
/// send again one that failed or was partly sent, and preview and apply a change of price and rules for one or many properties, all or nothing. A change is a process
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
    ICalendarChangeService changes,
    ICalendarChangeRunner runner,
    IOrgContextResolver orgContextResolver,
    IHostScopeResolver hostScopeResolver,
    IHostResourceLookup hostResources,
    IAuthorizationService authorizationService,
    IStringLocalizer<SharedResources> localizer,
    ILogger<CalendarChangesController> logger) : ControllerBase
{
    /// <summary>The header that says the list of a caller limited to some properties may leave out older changes of its own (<c>true</c>); absent otherwise.</summary>
    public const string TruncatedHeader = "X-Calendar-Changes-Truncated";

    /// <summary>
    /// The changes of the caller's org it reaches, newest first, at most 50, optionally only those in a <c>state</c> (<c>Applied</c>, <c>Queued</c>, <c>Sending</c>,
    /// <c>Sent</c>, <c>Confirmed</c>, <c>PartiallySent</c>, <c>Failed</c>). Each is <see cref="CalendarChangeDto"/>. A caller that reaches only some properties reads the 500
    /// newest changes of the org: when that was not enough to fill its list, the answer carries <see cref="TruncatedHeader"/> <c>true</c> (an older change of its own is still
    /// read by its id, and a <c>state</c> narrows the read).
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

        var list = await queries.ListAsync(scope, new CalendarChangeListQuery(state, limit ?? CalendarChangeListQuery.MaxLimit), cancellationToken);

        // A caller that reaches only some properties reads the newest changes of the org up to a bound: when the bound was reached before its list was full, older changes of its
        // own may be left out, and the answer says so (the body stays the list; the header is exposed to the browser by the CORS policy).
        if (list.Truncated)
            Response.Headers[TruncatedHeader] = "true";

        return Ok(list.Items.Select(view => CalendarChangeDto.Of(view, localizer)).ToList());
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

    /// <summary>
    /// What a change would do, and nothing else: per property how many days change, stay as they are, are taken or over, the first five changed days with their values before and
    /// after, whether the property is on the channel manager and the quota it would use (an estimate), with warnings (<c>property_not_mirrored</c>, <c>room_not_chosen</c>).
    /// The body is <see cref="CalendarChangeRequestDto"/>. 403 when one property cannot be written (nothing is read), 404 <c>property_not_found</c>, 422 with the stable code of
    /// what is refused (<c>calendar_day_rule_*</c>, <c>calendar_too_many_properties</c>, <c>calendar_change_no_properties</c>).
    /// </summary>
    [HttpPost("preview")]
    [Authorize(Policy = CasazenPolicies.PropertyWrite)]
    [ProducesResponseType(typeof(CalendarChangePreviewDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<CalendarChangePreviewDto>> Preview([FromBody] CalendarChangeRequestDto body, CancellationToken cancellationToken)
    {
        var prepared = await PrepareAsync(body, cancellationToken);
        if (prepared.Error is not null)
            return prepared.Error;

        var preview = await changes.PreviewAsync(prepared.Scope!, prepared.Request!, cancellationToken);
        return Ok(CalendarChangePreviewDto.Of(preview, localizer));
    }

    /// <summary>
    /// Applies the change to every property, all or nothing: the rules, the process and its chunks are saved in one transaction. 201 with the process (the shape of
    /// <c>GET /api/calendar/changes/{id}</c>) and its <c>Location</c>: <c>Queued</c> for a property that is on the channel manager (it is sent at once), <c>Applied</c> when
    /// the change stays in CasaZen. 403 when one property cannot be written (nothing is saved), 404 <c>property_not_found</c>, 422 <c>calendar_day_rule_nothing_to_change</c>
    /// (a change that changes nothing), <c>calendar_day_rule_in_past</c> (a range that is over) and the other codes of the preview.
    /// </summary>
    [HttpPost]
    [Authorize(Policy = CasazenPolicies.PropertyWrite)]
    [ProducesResponseType(typeof(CalendarChangeDto), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<CalendarChangeDto>> Apply([FromBody] CalendarChangeRequestDto body, CancellationToken cancellationToken)
    {
        var prepared = await PrepareAsync(body, cancellationToken);
        if (prepared.Error is not null)
            return prepared.Error;

        var scope = prepared.Scope!;
        var result = await changes.ApplyAsync(scope, prepared.Request!, User.GetUserId(), cancellationToken);
        logger.LogInformation(
            "Calendar change {ProcessId} applied by user {UserId}: {Properties} properties", result.ProcessId, User.GetUserId(), prepared.Request!.PropertyIds.Count);

        var view = await queries.GetAsync(scope, result.ProcessId, cancellationToken)
            ?? throw new InvalidOperationException($"The calendar change {result.ProcessId} just recorded cannot be read back.");
        return Created($"/api/calendar/changes/{result.ProcessId}", CalendarChangeDto.Of(view, localizer));
    }

    // --- Helpers -----------------------------------------------------------------------------------------------------------------

    private sealed record Prepared(HostScope? Scope, CalendarChangeRequest? Request, ActionResult? Error);

    /// <summary>
    /// The scope and the request of a preview or an application, or the answer to give instead: 400 for a body that cannot be a request, 403 for a caller with no reach, then
    /// what the request must look like (422, before anything is read), then the write right on every property (403 for the whole request, 404 for a property that is not found).
    /// </summary>
    private async Task<Prepared> PrepareAsync(CalendarChangeRequestDto body, CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid || !body.TryToRequest(out var request))
            return new Prepared(null, null, this.ApiProblem(StatusCodes.Status400BadRequest, ProblemCodes.BadRequest, "CalendarInvalidRequest"));

        if (await ScopeAsync(cancellationToken) is not { } scope)
            return new Prepared(null, null, this.ApiProblem(StatusCodes.Status403Forbidden, ProblemCodes.Forbidden, "Forbidden"));

        // The rules of the shape of the request first (a count of properties, the days, the weekdays, a patch that names a value): a request that cannot be applied
        // is refused before a property is looked up one by one.
        var ids = CalendarChangeRules.EnsureValid(request);
        var denied = await AuthorizeWriteAsync(ids, cancellationToken);
        return denied is not null ? new Prepared(null, null, denied) : new Prepared(scope, request, null);
    }

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
