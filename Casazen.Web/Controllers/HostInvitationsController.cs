using Casazen.Core.HostInvites;
using Casazen.Core.Services;
using Casazen.Core.Validation;
using Casazen.Web.Authorization;
using Casazen.Web.DTOs;
using Casazen.Web.DTOs.HostInvites;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Casazen.Web.Controllers;

/// <summary>
/// A host recommends CasaZen to a friend: an invite by email to a new independent host (a new org, not a member of its own).
/// Open to every member of a host org, whatever the role (a loyalty-style recommendation, not an administrative act): policy
/// <see cref="CasazenPolicies.SharedPropertyRead"/> is the permission every host role holds in either rental context, and
/// it applies only once the onboarding and the consents are done; suppliers and anyone without a host org never pass.
/// The org is the caller's own, never taken
/// from the request, and a host only sees, sends again and revokes the invites it created itself (another's id is a 404).
/// Limits per host org: <c>HostInvites:DailyQuotaPerHost</c> in 24 hours and <c>HostInvites:MaxPendingPerHost</c> pending.
/// </summary>
[ApiController]
[Route("api/hosts/me/invitations")]
[Authorize(Policy = CasazenPolicies.SharedPropertyRead)]
public class HostInvitationsController(IHostInviteService invites, IOrgContextResolver orgContextResolver) : ControllerBase
{
    /// <summary>
    /// Creates an invite and queues its email. 429 <c>host_invite_quota_exceeded</c> (daily quota) or <c>host_invite_pending_limit</c>,
    /// 409 <c>duplicate_invite</c>, 422 <c>host_invite_self</c>. An email that already has an account is answered exactly like
    /// any other (201, no email sent): an invite must not tell a host which addresses are registered.
    /// </summary>
    [HttpPost]
    [ProducesResponseType(typeof(HostInviteSentDto), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status422UnprocessableEntity)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status429TooManyRequests)]
    public async Task<ActionResult<HostInviteSentDto>> Create([FromBody] CreateHostInviteRequest request, CancellationToken cancellationToken)
    {
        var caller = await this.ResolveOrgTeamCallerAsync(orgContextResolver, cancellationToken);
        if (caller.Problem is not null)
            return caller.Problem;

        var sent = await invites.CreateAsync(
            new CreateHostInvite(Actor(caller), request.Email, request.Message, request.Locale), cancellationToken);
        return StatusCode(StatusCodes.Status201Created, HostInviteSentDto.From(sent));
    }

    /// <summary>The caller's own invites, newest first, paginated. <c>state</c> is <c>Pending</c>, <c>Used</c>, <c>Expired</c> or <c>Revoked</c>.</summary>
    [HttpGet]
    [ProducesResponseType(typeof(PagedResultDto<HostInviteDto>), StatusCodes.Status200OK)]
    public async Task<ActionResult<PagedResultDto<HostInviteDto>>> List(
        [FromQuery] string? search,
        [FromQuery] string? state,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        CancellationToken cancellationToken = default)
    {
        var caller = await this.ResolveOrgTeamCallerAsync(orgContextResolver, cancellationToken);
        if (caller.Problem is not null)
            return caller.Problem;

        page = Math.Max(page, 1);
        pageSize = Math.Clamp(pageSize, 1, 100);
        HostInviteState? stateFilter = EnumNames.TryParseDefined<HostInviteState>(state, out var parsed) ? parsed : null;

        var (items, total) = await invites.ListAsync(
            new HostInviteListQuery(Actor(caller), search, stateFilter, page, pageSize), cancellationToken);
        return Ok(new PagedResultDto<HostInviteDto>
        {
            Items = items.Select(HostInviteDto.From).ToList(),
            TotalCount = total,
            Page = page,
            PageSize = pageSize,
        });
    }

    /// <summary>The daily quota of the caller's org: limit, used, remaining and when the oldest use leaves the 24-hour window.</summary>
    [HttpGet("quota")]
    [ProducesResponseType(typeof(HostInviteQuotaDto), StatusCodes.Status200OK)]
    public async Task<ActionResult<HostInviteQuotaDto>> GetQuota(CancellationToken cancellationToken)
    {
        var caller = await this.ResolveOrgTeamCallerAsync(orgContextResolver, cancellationToken);
        if (caller.Problem is not null)
            return caller.Problem;

        return Ok(HostInviteQuotaDto.From(await invites.GetQuotaAsync(caller.OrgId, cancellationToken)));
    }

    /// <summary>
    /// Sends an invite of the caller again: a new link and a new 7-day expiry, the old link stops working. It counts against the
    /// daily quota. 404 <c>host_invite_not_found</c>, 409 <c>host_invite_not_resendable</c>, <c>duplicate_invite</c>, 429.
    /// </summary>
    [HttpPost("{id:guid}/resend")]
    [ProducesResponseType(typeof(HostInviteSentDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status429TooManyRequests)]
    public async Task<ActionResult<HostInviteSentDto>> Resend(Guid id, CancellationToken cancellationToken)
    {
        var caller = await this.ResolveOrgTeamCallerAsync(orgContextResolver, cancellationToken);
        if (caller.Problem is not null)
            return caller.Problem;

        return Ok(HostInviteSentDto.From(await invites.ResendAsync(Actor(caller), id, cancellationToken)));
    }

    /// <summary>Revokes a pending invite of the caller. 404 <c>host_invite_not_found</c>, 409 <c>host_invite_not_pending</c>.</summary>
    [HttpDelete("{id:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Revoke(Guid id, CancellationToken cancellationToken)
    {
        var caller = await this.ResolveOrgTeamCallerAsync(orgContextResolver, cancellationToken);
        if (caller.Problem is not null)
            return caller.Problem;

        await invites.RevokeAsync(Actor(caller), id, cancellationToken);
        return NoContent();
    }

    private static HostInviteActor Actor(OrgTeamCaller caller) => new(caller.UserId, caller.OrgId, IsAdmin: false);
}
