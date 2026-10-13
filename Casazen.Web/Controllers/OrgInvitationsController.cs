using Casazen.Core.Features;
using Casazen.Core.Services;
using Casazen.Web.Authorization;
using Casazen.Web.DTOs.Orgs;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Casazen.Web.Controllers;

/// <summary>
/// The invitations of the caller's org (AM-02): invite a person with a role, list the invitations still open, send one
/// again, withdraw it, copy its link. For the owner and the administrators (policy <see cref="CasazenPolicies.OrgMembersManage"/>,
/// the permission <c>org.members.manage</c> of the <c>account</c> context); only the owner invites an administrator. Behind
/// the <c>OrgTeam</c> flag: 404 while it is off. The org is the caller's own, never taken from the request.
/// Runbook <c>docs/runbooks/org-team.md</c>.
/// </summary>
[ApiController]
[Route("api/orgs/me/invitations")]
[FeatureGate(FeatureFlags.OrgTeam)]
[Authorize(Policy = CasazenPolicies.OrgMembersManage)]
public class OrgInvitationsController(
    IOrgInvitationService invitations,
    IOrgContextResolver orgContextResolver) : ControllerBase
{
    /// <summary>
    /// Invites a person. The email goes out at once (<c>emailQueued</c> says whether it could be queued); the invitation holds
    /// a seat until it is accepted, revoked or expires. 403 <c>org_owner_required</c> (an administrator invites an
    /// administrator), 409 <c>org_seat_limit_reached</c> (no free seat on the plan), <c>org_invitation_already_pending</c>,
    /// <c>org_member_already_member</c>, 422 <c>org_owner_not_assignable</c>, <c>org_member_area_required</c>.
    /// </summary>
    [HttpPost]
    [ProducesResponseType(typeof(OrgInvitationSentDto), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<OrgInvitationSentDto>> Create(
        [FromBody] CreateOrgInvitationRequest request,
        CancellationToken cancellationToken)
    {
        var caller = await this.ResolveOrgTeamCallerAsync(orgContextResolver, cancellationToken);
        if (caller.Problem is not null)
            return caller.Problem;

        var sent = await invitations.CreateAsync(
            new CreateOrgInvitation(
                caller.OrgId,
                caller.UserId,
                request.Email,
                request.Name,
                request.Role,
                request.Areas,
                request.PropertyScope,
                request.Language),
            cancellationToken);

        return StatusCode(StatusCodes.Status201Created, OrgInvitationSentDto.From(sent));
    }

    /// <summary>The org's invitations that are open or expired (the ones that can be sent again), newest first. Never the token.</summary>
    [HttpGet]
    [ProducesResponseType(typeof(IReadOnlyList<OrgInvitationDto>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<OrgInvitationDto>>> List(CancellationToken cancellationToken)
    {
        var caller = await this.ResolveOrgTeamCallerAsync(orgContextResolver, cancellationToken);
        if (caller.Problem is not null)
            return caller.Problem;

        var items = await invitations.ListAsync(caller.OrgId, cancellationToken);
        return Ok(items.Select(OrgInvitationDto.From).ToList());
    }

    /// <summary>
    /// Sends the invitation again: a new link (the previous one, the one of the first email, stops working) and seven more
    /// days from now. An expired invitation comes back and takes a seat again. 404 <c>org_invitation_not_found</c>, 409
    /// <c>org_invitation_not_pending</c> (accepted or revoked), <c>org_seat_limit_reached</c>, <c>org_invitation_already_pending</c>.
    /// </summary>
    [HttpPost("{id:guid}/resend")]
    [ProducesResponseType(typeof(OrgInvitationSentDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<ActionResult<OrgInvitationSentDto>> Resend(Guid id, CancellationToken cancellationToken)
    {
        var caller = await this.ResolveOrgTeamCallerAsync(orgContextResolver, cancellationToken);
        if (caller.Problem is not null)
            return caller.Problem;

        var sent = await invitations.ResendAsync(caller.OrgId, id, caller.UserId, cancellationToken);
        return Ok(OrgInvitationSentDto.From(sent));
    }

    /// <summary>
    /// Withdraws the invitation: its link stops working and the seat is free again. Idempotent. 404
    /// <c>org_invitation_not_found</c>, 409 <c>org_invitation_not_pending</c> (already accepted).
    /// </summary>
    [HttpPost("{id:guid}/revoke")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Revoke(Guid id, CancellationToken cancellationToken)
    {
        var caller = await this.ResolveOrgTeamCallerAsync(orgContextResolver, cancellationToken);
        if (caller.Problem is not null)
            return caller.Problem;

        await invitations.RevokeAsync(caller.OrgId, id, caller.UserId, cancellationToken);
        return NoContent();
    }

    /// <summary>
    /// A fresh link to give to the invited person by hand ("copy link"). It rotates the token: the link of the email stops
    /// working. Neither the expiry nor the reminder change, and no email goes out. The response is never cached. 404
    /// <c>org_invitation_not_found</c>, 409 <c>org_invitation_not_pending</c> (also once expired: send it again).
    /// </summary>
    [HttpPost("{id:guid}/link")]
    [ProducesResponseType(typeof(OrgInvitationLinkDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<ActionResult<OrgInvitationLinkDto>> CopyLink(Guid id, CancellationToken cancellationToken)
    {
        var caller = await this.ResolveOrgTeamCallerAsync(orgContextResolver, cancellationToken);
        if (caller.Problem is not null)
            return caller.Problem;

        var link = await invitations.CopyLinkAsync(caller.OrgId, id, caller.UserId, cancellationToken);
        Response.Headers.CacheControl = "no-store";
        return Ok(OrgInvitationLinkDto.From(link));
    }
}
