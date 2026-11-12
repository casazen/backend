using Casazen.Core.Authorization;
using Casazen.Core.Features;
using Casazen.Core.Services;
using Casazen.Web.Authorization;
using Casazen.Web.DTOs.OwnerInvitations;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Casazen.Web.Controllers;

/// <summary>
/// The invitation of the property owners of an agency to the owner portal, as a controlled batch (E10/F02/US02/T02, NF-109, ADR-011): a server
/// <b>preview</b> of who would be invited and who is left out and why, an explicit <b>confirmation</b> that carries the fingerprint of the preview and
/// is judged again, the lifecycle of every owner (<c>notReady</c>, <c>ready</c>, <c>invited</c>, <c>accepted</c>, <c>expired</c>, <c>revoked</c>), and
/// the resend and the withdrawal of a link. <b>The owner import never calls any of it.</b>
/// </summary>
/// <remarks>
/// <para><b>Policy.</b> <see cref="CasazenPolicies.AgencyOwnerManage"/> (<c>agency.owner.manage</c>: the owner and the administrators of an org on the
/// agency plan), the policy of the single invitation too. The calls look at the caller's reach (<c>HostScope</c>): an owner of another org, one
/// outside the reach and one that does not exist are one and the same answer. At most 100 invitations a day for each org (429).</para>
/// <para>Behind <c>Features:OctorateOnboarding</c>: 404 while it is off, before authentication. Every answer is private and never cached.</para>
/// </remarks>
[ApiController]
[Route("api/onboarding/owners/invitations")]
[FeatureGate(FeatureFlags.OctorateOnboarding)]
[Authorize(Policy = CasazenPolicies.AgencyOwnerManage)]
[PrivateAnswer]
public sealed class OwnerInvitationBatchController(
    IOwnerInvitationService invitations,
    IOrgContextResolver orgContextResolver,
    IHostScopeResolver hostScopeResolver) : ControllerBase
{
    /// <summary>Every owner inside the reach with the state of its invitation.</summary>
    [HttpGet]
    [ProducesResponseType(typeof(IReadOnlyList<OwnerInvitationStatusDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<IReadOnlyList<OwnerInvitationStatusDto>>> List(CancellationToken cancellationToken)
    {
        var scope = await ResolveScopeAsync(cancellationToken);
        if (scope is null)
            return this.ApiProblem(StatusCodes.Status403Forbidden, ProblemCodes.Forbidden, "Forbidden");

        var items = await invitations.ListAsync(scope, cancellationToken);
        return Ok(items.Select(OwnerInvitationStatusDto.From).ToList());
    }

    /// <summary>
    /// The batch that confirming would send: nothing is sent. 422 <c>owner_invitation_selection_empty</c> for an empty list of owners (never «all»).
    /// </summary>
    [HttpPost("preview")]
    [ProducesResponseType(typeof(OwnerInvitationPreviewDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status422UnprocessableEntity)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<OwnerInvitationPreviewDto>> Preview(
        [FromBody] OwnerInvitationPreviewRequest? request,
        CancellationToken cancellationToken)
    {
        var scope = await ResolveScopeAsync(cancellationToken);
        if (scope is null)
            return this.ApiProblem(StatusCodes.Status403Forbidden, ProblemCodes.Forbidden, "Forbidden");

        var preview = await invitations.PreviewBatchAsync(scope, request?.OwnerIds, cancellationToken);
        return Ok(OwnerInvitationPreviewDto.From(preview));
    }

    /// <summary>
    /// Sends the batch of a preview. Needs the <c>fingerprint</c> of the preview (422 <c>owner_invitation_preview_required</c>), judges the batch again
    /// (409 <c>owner_invitation_preview_stale</c> when the owners changed since), refuses a batch with no recipient (422 <c>owner_invitation_nothing_to_send</c>)
    /// and one that does not fit the limit of the day (429 <c>owner_invitation_daily_limit_reached</c>: none is sent).
    /// </summary>
    [HttpPost("confirm")]
    [ProducesResponseType(typeof(OwnerInvitationBatchResultDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status422UnprocessableEntity)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status429TooManyRequests)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<OwnerInvitationBatchResultDto>> Confirm(
        [FromBody] OwnerInvitationConfirmRequest request,
        CancellationToken cancellationToken)
    {
        var userId = User.GetUserId();
        var scope = await ResolveScopeAsync(cancellationToken);
        if (scope is null || string.IsNullOrWhiteSpace(userId))
            return this.ApiProblem(StatusCodes.Status403Forbidden, ProblemCodes.Forbidden, "Forbidden");

        var result = await invitations.ConfirmBatchAsync(scope, userId, request.OwnerIds, request.Fingerprint, cancellationToken);
        return Ok(OwnerInvitationBatchResultDto.From(result));
    }

    /// <summary>
    /// Sends the owner of the invitation a new link and withdraws the one in force. 404 <c>owner_invitation_not_found</c>; 409 <c>owner_invitation_used</c>
    /// when the owner has accepted; 422 <c>owner_invitation_email_not_queued</c>; 429 at the limit of the day.
    /// </summary>
    [HttpPost("{invitationId:guid}/resend")]
    [ProducesResponseType(typeof(OwnerInvitationStatusDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status429TooManyRequests)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<OwnerInvitationStatusDto>> Resend(Guid invitationId, CancellationToken cancellationToken)
    {
        var userId = User.GetUserId();
        var scope = await ResolveScopeAsync(cancellationToken);
        if (scope is null || string.IsNullOrWhiteSpace(userId))
            return this.ApiProblem(StatusCodes.Status403Forbidden, ProblemCodes.Forbidden, "Forbidden");

        return Ok(OwnerInvitationStatusDto.From(await invitations.ResendAsync(scope, userId, invitationId, cancellationToken)));
    }

    /// <summary>
    /// Withdraws an invitation in force: its link stops working. A second withdrawal answers the same and writes nothing. 404
    /// <c>owner_invitation_not_found</c>; 409 <c>owner_invitation_used</c> (accepted) or <c>owner_invitation_not_live</c> (lapsed).
    /// </summary>
    [HttpPost("{invitationId:guid}/revoke")]
    [ProducesResponseType(typeof(OwnerInvitationStatusDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<OwnerInvitationStatusDto>> Revoke(Guid invitationId, CancellationToken cancellationToken)
    {
        var userId = User.GetUserId();
        var scope = await ResolveScopeAsync(cancellationToken);
        if (scope is null || string.IsNullOrWhiteSpace(userId))
            return this.ApiProblem(StatusCodes.Status403Forbidden, ProblemCodes.Forbidden, "Forbidden");

        return Ok(OwnerInvitationStatusDto.From(await invitations.RevokeAsync(scope, userId, invitationId, cancellationToken)));
    }

    private async Task<HostScope?> ResolveScopeAsync(CancellationToken cancellationToken)
    {
        var orgId = await orgContextResolver.GetOrProvisionOrgIdAsync(cancellationToken);
        return orgId is null ? null : await hostScopeResolver.ResolveHostScopeAsync(User, orgId.Value, cancellationToken);
    }
}
