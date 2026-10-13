using Casazen.Core.HostInvites;
using Casazen.Core.Services;
using Casazen.Core.Validation;
using Casazen.Web.Authorization;
using Casazen.Web.DTOs;
using Casazen.Web.DTOs.HostInvites;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Casazen.Web.Controllers;

/// <summary>
/// Platform-admin endpoints to invite new independent hosts by email. The invite grants no role: the person signs up normally.
/// Admins have no daily quota and see every invite, with who invited it.
/// </summary>
[ApiController]
[Route("api/admin/host-invites")]
[Authorize(Policy = CasazenPolicies.AdminOnly)]
public class AdminHostInvitesController(IHostInviteService invites) : ControllerBase
{
    /// <summary>
    /// Creates an invite and queues its email. 409 <c>duplicate_invite</c> (a pending invite of the admin for the email),
    /// <c>host_email_taken</c> (an account already has the email), 422 <c>host_invite_self</c>.
    /// </summary>
    [HttpPost]
    [ProducesResponseType(typeof(HostInviteSentDto), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<HostInviteSentDto>> Create([FromBody] CreateHostInviteRequest request, CancellationToken cancellationToken)
    {
        var sent = await invites.CreateAsync(
            new CreateHostInvite(Actor(), request.Email, request.Message, request.Locale), cancellationToken);
        return StatusCode(StatusCodes.Status201Created, HostInviteSentDto.From(sent));
    }

    /// <summary>
    /// The invites, newest first, paginated in SQL. <c>state</c> is <c>Pending</c>, <c>Used</c>, <c>Expired</c> or <c>Revoked</c>
    /// (anything else leaves the filter off); <c>search</c> matches the invited email.
    /// </summary>
    [HttpGet]
    [ProducesResponseType(typeof(PagedResultDto<HostInviteDto>), StatusCodes.Status200OK)]
    public async Task<ActionResult<PagedResultDto<HostInviteDto>>> List(
        [FromQuery] string? search,
        [FromQuery] string? state,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        CancellationToken cancellationToken = default)
    {
        page = Math.Max(page, 1);
        pageSize = Math.Clamp(pageSize, 1, 100);
        HostInviteState? stateFilter = EnumNames.TryParseDefined<HostInviteState>(state, out var parsed) ? parsed : null;

        var (items, total) = await invites.ListAsync(new HostInviteListQuery(Actor(), search, stateFilter, page, pageSize), cancellationToken);
        return Ok(new PagedResultDto<HostInviteDto>
        {
            Items = items.Select(HostInviteDto.From).ToList(),
            TotalCount = total,
            Page = page,
            PageSize = pageSize,
        });
    }

    /// <summary>
    /// Sends a pending or expired invite again: a new link and a new 7-day expiry, the old link stops working. 404
    /// <c>host_invite_not_found</c>, 409 <c>host_invite_not_resendable</c> (used or revoked), <c>duplicate_invite</c>,
    /// <c>host_email_taken</c>.
    /// </summary>
    [HttpPost("{id:guid}/resend")]
    [ProducesResponseType(typeof(HostInviteSentDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<ActionResult<HostInviteSentDto>> Resend(Guid id, CancellationToken cancellationToken) =>
        Ok(HostInviteSentDto.From(await invites.ResendAsync(Actor(), id, cancellationToken)));

    /// <summary>Revokes a pending invite: its link no longer works. 404 <c>host_invite_not_found</c>, 409 <c>host_invite_not_pending</c>.</summary>
    [HttpDelete("{id:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Revoke(Guid id, CancellationToken cancellationToken)
    {
        await invites.RevokeAsync(Actor(), id, cancellationToken);
        return NoContent();
    }

    /// <summary>AdminOnly guarantees an authenticated user, whose token always has a subject.</summary>
    private HostInviteActor Actor() =>
        new(User.GetUserId() ?? throw new UnauthorizedAccessException("Admin without subject claim"), null, IsAdmin: true);
}
