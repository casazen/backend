using Casazen.Core.Authorization;
using Casazen.Core.Multitenancy;
using Casazen.Core.Services;
using Casazen.Web.Authorization;
using Casazen.Web.DTOs;
using Casazen.Web.DTOs.ApprovalQueue;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Casazen.Web.Controllers;

/// <summary>
/// Approval queue for items requiring human decision before taking effect (NF-06), and a decision that executes (E05/F04/US01/T02, NF-68, ADR-032). Every action needs
/// <c>approval.decide</c> (<see cref="CasazenPolicies.ApprovalDecide"/>: the owner, the administrators and the property managers; no longer the broad <c>property.read</c>
/// and <c>property.write</c>, which a collaborator holds), and what a caller reaches follows its <see cref="HostScope"/> in SQL: an item of another org or of a property out of its
/// reach is a 404, never a 403. The answers carry the text of messages to guests, so they are private and never cached, and no text and no reason is ever logged.
/// </summary>
/// <remarks>
/// 404 <c>approval_item_not_found</c>; 409 <c>approval_item_not_pending</c> (already rejected or expired), <c>approval_origin_action_failed</c> (the action failed, the item stays
/// pending, the extension <c>originActionError</c> says why); 422 <c>approval_edit_invalid</c>, <c>approval_rejection_reason_required</c>.
/// </remarks>
[ApiController]
[Route("api/approval-queue")]
[Authorize(Policy = CasazenPolicies.ApprovalDecide)]
[PrivateAnswer]
public class ApprovalQueueController(
    IApprovalQueueService approvalQueueService,
    ITenantContext tenantContext,
    IHostScopeResolver hostScopeResolver,
    ILogger<ApprovalQueueController> logger) : ControllerBase
{
    /// <summary>
    /// Get pending approval items for the caller's organization, narrowed to the properties the caller reaches (an item with no property only for a caller who reaches every one),
    /// oldest first, with the score, the situation and the thread key of a draft of a message.
    /// </summary>
    [HttpGet]
    [ProducesResponseType(typeof(IEnumerable<ApprovalQueueItemDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<IEnumerable<ApprovalQueueItemDto>>> GetPendingItems(CancellationToken cancellationToken)
    {
        var scope = await ResolveScopeAsync(cancellationToken);
        if (scope.Problem is not null)
            return scope.Problem;

        var items = await approvalQueueService.GetPendingAsync(scope.Value!, cancellationToken);

        return Ok(items.Select(i => ApprovalQueueItemDto.From(i)));
    }

    /// <summary>
    /// Edit the text of a pending item: the original is kept, and the approval acts with the edited one. 422 <c>approval_edit_invalid</c> for an empty text, a message longer than
    /// 2000 characters or a campaign that is not a subject line, a blank line and a body.
    /// </summary>
    [HttpPut("{id}/edit")]
    [RequestSizeLimit(262144)]
    [ProducesResponseType(typeof(ApprovalQueueItemDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<ApprovalQueueItemDto>> EditItem(Guid id, [FromBody] EditApprovalItemDto dto, CancellationToken cancellationToken)
    {
        var scope = await ResolveScopeAsync(cancellationToken);
        if (scope.Problem is not null)
            return scope.Problem;

        var userId = User.GetUserId();
        if (string.IsNullOrEmpty(userId))
            return UserIdMissing();

        var item = await approvalQueueService.EditAsync(scope.Value!, id, dto.Text, userId, cancellationToken);

        logger.LogInformation("User {UserId} edited approval queue item {ItemId} of type {ItemType}", userId, id, item.ItemType);

        return Ok(ApprovalQueueItemDto.From(item));
    }

    /// <summary>
    /// Approve a pending approval queue item: the action it was waiting for runs (a message goes to the guest once, a campaign is approved and scheduled when it has a time), then the
    /// item is approved. 200 with the item and <c>originActionResult</c>; 409 <c>approval_origin_action_failed</c> when the action failed (the item stays pending and the host can edit
    /// and try again); an item already approved answers 200 as it is and sends nothing again.
    /// </summary>
    [HttpPost("{id}/approve")]
    [ProducesResponseType(typeof(ApprovalQueueItemDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<ApprovalQueueItemDto>> ApproveItem(Guid id, CancellationToken cancellationToken)
    {
        var scope = await ResolveScopeAsync(cancellationToken);
        if (scope.Problem is not null)
            return scope.Problem;

        var userId = User.GetUserId();
        if (string.IsNullOrEmpty(userId))
            return UserIdMissing();

        var decision = await approvalQueueService.ApproveAsync(scope.Value!, id, userId, cancellationToken);

        logger.LogInformation(
            "User {UserId} approved approval queue item {ItemId} of type {ItemType}: action {OriginAction} {State}",
            userId,
            id,
            decision.Item.ItemType,
            decision.Item.OriginAction,
            decision.ActionState ?? "(none)");

        return Ok(ApprovalQueueItemDto.From(decision.Item, decision.ActionState));
    }

    /// <summary>
    /// Reject a pending approval queue item. The reason is mandatory (422 <c>approval_rejection_reason_required</c>, at most 500 characters) and is saved on the item; the rejection hook
    /// of the type runs (a campaign goes back to a draft).
    /// </summary>
    [HttpPost("{id}/reject")]
    [ProducesResponseType(typeof(ApprovalQueueItemDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<ApprovalQueueItemDto>> RejectItem(
        Guid id,
        [FromBody] RejectApprovalItemDto dto,
        CancellationToken cancellationToken)
    {
        var scope = await ResolveScopeAsync(cancellationToken);
        if (scope.Problem is not null)
            return scope.Problem;

        var userId = User.GetUserId();
        if (string.IsNullOrEmpty(userId))
            return UserIdMissing();

        var item = await approvalQueueService.RejectAsync(scope.Value!, id, userId, dto.Reason, cancellationToken);

        // The reason is the host's own words: it is never logged.
        logger.LogInformation("User {UserId} rejected approval queue item {ItemId} of type {ItemType}", userId, id, item.ItemType);

        return Ok(ApprovalQueueItemDto.From(item));
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

    private ObjectResult UserIdMissing() =>
        this.ApiProblem(StatusCodes.Status401Unauthorized, ProblemCodes.Unauthorized, "UserIdMissing");
}
