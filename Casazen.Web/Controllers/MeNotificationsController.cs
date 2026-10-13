using Casazen.Core.Entities;
using Casazen.Core.Features;
using Casazen.Core.Services;
using Casazen.Web.Authorization;
using Casazen.Web.DTOs;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Casazen.Web.Controllers;

/// <summary>
/// The in-app notifications of the caller, for the bell of the shell (UI-12a): the list, the unread count, and the two ways to
/// clear them. Behind <see cref="FeatureFlags.InAppNotifications"/>: off, every action answers 404 like a route that does not exist,
/// before authentication. Runbook: <c>docs/runbooks/in-app-notifications.md</c>.
/// </summary>
/// <remarks>
/// <para>TN-3. The notifications are the caller's own, so the policy is <see cref="CasazenPolicies.Authenticated"/> (every context
/// has a bell, host, landlord and supplier alike) and the user is always the token's <c>sub</c>, never a parameter. Which rows
/// the caller may reach is decided in one place, <see cref="IInAppNotificationService"/>: its own rows in the orgs it belongs to.
/// The id of a notification of somebody else, or of an org the caller has left, is a 404, never a 403, the same answer as an id
/// that does not exist.</para>
/// <para>The answers are private and never cached (<c>Cache-Control: private, no-store</c>): the bell polls them. There is no
/// rate limit policy of its own, like the other <c>api/me</c> endpoints (the policies of <c>RateLimitPolicies</c> are the ones of the
/// anonymous endpoints, per client IP): every call is an indexed read or a single update of the caller's rows.</para>
/// </remarks>
[ApiController]
[Route("api/me/notifications")]
[Authorize(Policy = CasazenPolicies.Authenticated)]
[FeatureGate(FeatureFlags.InAppNotifications)]
public class MeNotificationsController(IInAppNotificationService notifications) : ControllerBase
{
    /// <summary>
    /// The caller's notifications, the most recent first. <paramref name="unread"/> lists only the unread ones;
    /// <paramref name="pageSize"/> is held between 1 and 50 (default 20), <paramref name="page"/> at 1 or more.
    /// </summary>
    /// <response code="200">A page (<c>items</c>, <c>totalCount</c>, <c>page</c>, <c>pageSize</c>).</response>
    [HttpGet]
    [ProducesResponseType(typeof(PagedResultDto<InAppNotificationDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<PagedResultDto<InAppNotificationDto>>> List(
        [FromQuery] bool unread = false,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = InAppNotificationLimits.DefaultPageSize,
        CancellationToken cancellationToken = default)
    {
        if (User.GetUserId() is not { Length: > 0 } userId)
            return Unauthorized();

        var result = await notifications.ListAsync(userId, unread, page, pageSize, cancellationToken);
        Response.Headers.CacheControl = "private, no-store";
        return Ok(new PagedResultDto<InAppNotificationDto>
        {
            Items = result.Items.Select(InAppNotificationDto.From).ToList(),
            TotalCount = result.TotalCount,
            Page = result.Page,
            PageSize = result.PageSize,
        });
    }

    /// <summary>How many of the caller's notifications are unread: the number on the bell.</summary>
    /// <response code="200"><c>{ count }</c>.</response>
    [HttpGet("unread-count")]
    [ProducesResponseType(typeof(UnreadNotificationCountDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<UnreadNotificationCountDto>> UnreadCount(CancellationToken cancellationToken)
    {
        if (User.GetUserId() is not { Length: > 0 } userId)
            return Unauthorized();

        var count = await notifications.CountUnreadAsync(userId, cancellationToken);
        Response.Headers.CacheControl = "private, no-store";
        return Ok(new UnreadNotificationCountDto(count));
    }

    /// <summary>
    /// Marks one notification as read. Idempotent: one already read answers 204 too.
    /// </summary>
    /// <response code="204">Read (or already read).</response>
    /// <response code="404">The caller has no such notification: another user's, another org's, deleted by the retention or never existed.</response>
    [HttpPost("{id:guid}/read")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> MarkRead(Guid id, CancellationToken cancellationToken)
    {
        if (User.GetUserId() is not { Length: > 0 } userId)
            return Unauthorized();

        await notifications.MarkReadAsync(userId, id, cancellationToken);
        return NoContent();
    }

    /// <summary>Marks every unread notification of the caller as read. Idempotent.</summary>
    /// <response code="204">Done (also when there was nothing to mark).</response>
    [HttpPost("read-all")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> MarkAllRead(CancellationToken cancellationToken)
    {
        if (User.GetUserId() is not { Length: > 0 } userId)
            return Unauthorized();

        await notifications.MarkAllReadAsync(userId, cancellationToken);
        return NoContent();
    }
}
