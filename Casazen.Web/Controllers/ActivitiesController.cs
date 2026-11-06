using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Services;
using Casazen.Web.Authorization;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Casazen.Web.Controllers;

/// <summary>
/// The caller's "To do" list (ADR-026, NF-07): the open items of its org that its scope reaches, the number of the urgent ones
/// for the nav badge, and the manual close. The items are raised and closed by the system through
/// <see cref="IInternalActivityService"/> (<c>CreateAsync</c>, <c>CloseAsync</c>); here they are only read and closed by hand.
/// </summary>
/// <remarks>
/// <para>The list and the count are the caller's own, in every host context, so their policy is
/// <see cref="CasazenPolicies.Authenticated"/> and what the caller reaches is its <see cref="HostScope"/>, resolved from its org
/// membership in the database (<see cref="IHostScopeResolver"/>), never from the request: an owner, an administrator, a manager
/// or a collaborator with every property sees the whole org; a collaborator «Solo alcuni» only the items of the properties it
/// was given, never an organization-level item (ADR-026-R2). A caller with no org or no scope gets an empty list.</para>
/// <para>Closing an item is a change, not a read: the close adds <see cref="CasazenPolicies.PropertyWrite"/> (<c>property.write</c>
/// of the short-rent context) to the policy of the class. A role that only reads (the accountant, a collaborator with no write
/// permission) still reads the list and the count but cannot close an item for the whole org, a deadline of law such as
/// Alloggiati or ISTAT included, which would then vanish for the owner too. The policy says who may close; the scope above still
/// says which items the caller reaches. Like every endpoint, the role is decided here in the web layer:
/// <see cref="IInternalActivityService"/> checks no role.</para>
/// <para>The answer is <see cref="TodoActivityDto"/>, never the <see cref="InternalActivity"/> entity: no staff user, rate, note
/// or person (ADR-026-R3); the client reads the detail from the API of the entity. It is private and never cached. This
/// controller is not <c>InternalActivitiesController</c>, the agency-plan staff API, which stays as it is.</para>
/// </remarks>
[ApiController]
[Route("api/me/activities")]
[Authorize(Policy = CasazenPolicies.Authenticated)]
public class ActivitiesController(
    IInternalActivityService activities,
    IHostScopeResolver hostScopeResolver,
    IOrgContextResolver orgContextResolver) : ControllerBase
{
    /// <summary>
    /// The open "To do" items the caller reaches, sorted by the server: the most urgent first, then by deadline (the ones
    /// without a deadline last), then by creation; at most <see cref="TodoLimits.MaxPendingItems"/>.
    /// </summary>
    /// <response code="200">The items (an empty array when the caller has no org or no scope).</response>
    [HttpGet]
    [ProducesResponseType(typeof(IReadOnlyList<TodoActivityDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<IReadOnlyList<TodoActivityDto>>> List(CancellationToken cancellationToken)
    {
        if (User.GetUserId() is not { Length: > 0 })
            return Unauthorized();

        Response.Headers.CacheControl = "private, no-store";
        if (await ScopeAsync(cancellationToken) is not { } scope)
            return Ok(Array.Empty<TodoActivityDto>());

        var items = await activities.GetPendingAsync(scope, cancellationToken);
        return Ok(items.Select(TodoActivityDto.From).ToList());
    }

    /// <summary>How many of the caller's open items are urgent (<see cref="ActivityUrgency.High"/>): the number on the nav badge.</summary>
    /// <response code="200"><c>{ count }</c>.</response>
    [HttpGet("count")]
    [ProducesResponseType(typeof(TodoCountDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<TodoCountDto>> UrgentCount(CancellationToken cancellationToken)
    {
        if (User.GetUserId() is not { Length: > 0 })
            return Unauthorized();

        Response.Headers.CacheControl = "private, no-store";
        if (await ScopeAsync(cancellationToken) is not { } scope)
            return Ok(new TodoCountDto(0));

        return Ok(new TodoCountDto(await activities.CountUrgentAsync(scope, cancellationToken)));
    }

    /// <summary>
    /// Closes one item of the caller's list by hand (<c>ClosedBy</c> = the caller). Idempotent: an item already closed answers
    /// 204 too. It needs <see cref="CasazenPolicies.PropertyWrite"/> on top of the class policy: a caller that only reads is
    /// refused with 403 before anything is looked up, and one that writes closes only the items its scope reaches.
    /// </summary>
    /// <response code="204">Closed (or already closed).</response>
    /// <response code="403">The caller does not hold <c>property.write</c> in the short-rent context (a role that only reads).</response>
    /// <response code="404">The caller reaches no such item: of a property it was not given, of another org, unknown.</response>
    [HttpPost("{id:guid}/close")]
    [Authorize(Policy = CasazenPolicies.PropertyWrite)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Close(Guid id, CancellationToken cancellationToken)
    {
        if (User.GetUserId() is not { Length: > 0 } userId)
            return Unauthorized();

        if (await ScopeAsync(cancellationToken) is not { } scope)
            throw new NotFoundException("To do not found.");

        await activities.CloseInScopeAsync(id, scope, userId, cancellationToken);
        return NoContent();
    }

    /// <summary>The caller's scope on its org, or <c>null</c> when it has no org or reaches nothing (never widened).</summary>
    private async Task<HostScope?> ScopeAsync(CancellationToken cancellationToken)
    {
        var orgId = await orgContextResolver.GetOrProvisionOrgIdAsync(cancellationToken);
        return orgId is null ? null : await hostScopeResolver.ResolveHostScopeAsync(User, orgId.Value, cancellationToken);
    }
}

/// <summary>
/// An item of the "To do" list as the API answers it (ADR-026-R3): what it is, where its "Do now" leads and what it is about,
/// by type and id. No personal data, no staff data: the person it is for, the rate, the notes and who closed it stay in the
/// database.
/// </summary>
/// <param name="Id">The item, to close it.</param>
/// <param name="ActivityType">What it is: the name of a <see cref="Casazen.Core.Entities.Enums.ActivityType"/> member.</param>
/// <param name="Action">Where its "Do now" button leads (<c>ACTION_ROUTE_MAP</c> of the frontend).</param>
/// <param name="EntityType">The kind of entity it is about (<c>Booking</c>, ...).</param>
/// <param name="EntityId">The id of that entity.</param>
/// <param name="PropertyId">The property, <c>null</c> for an organization-level item.</param>
/// <param name="DueAt">The deadline (UTC), when it has one.</param>
/// <param name="Urgency">How urgent it is.</param>
/// <param name="Reason">Why it was raised, one line (a code), never personal data.</param>
/// <param name="CreatedAt">When it was raised (UTC).</param>
public sealed record TodoActivityDto(
    Guid Id,
    string ActivityType,
    ActivityAction Action,
    string EntityType,
    Guid EntityId,
    Guid? PropertyId,
    DateTime? DueAt,
    ActivityUrgency Urgency,
    string? Reason,
    DateTime CreatedAt)
{
    /// <summary>The item of a "To do" row (one that <see cref="IInternalActivityService.GetPendingAsync"/> returns).</summary>
    public static TodoActivityDto From(InternalActivity activity)
    {
        ArgumentNullException.ThrowIfNull(activity);
        if (activity.Action is not { } action)
            throw new InvalidOperationException($"Activity {activity.Id} is staff work, not a To do item.");

        return new TodoActivityDto(
            activity.Id,
            activity.ActivityType,
            action,
            activity.EntityType ?? string.Empty,
            activity.EntityId ?? Guid.Empty,
            activity.PropertyId,
            activity.Deadline,
            activity.Urgency ?? ActivityUrgency.Low,
            activity.Reason,
            activity.CreatedAt);
    }
}

/// <summary>The number of urgent items of the caller's "To do" list: <c>{ count }</c>.</summary>
public sealed record TodoCountDto(int Count);
