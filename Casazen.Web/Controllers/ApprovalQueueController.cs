using Casazen.Core.Authorization;
using Casazen.Core.Multitenancy;
using Casazen.Core.Services;
using Casazen.Web.Authorization;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Casazen.Web.DTOs;
using Casazen.Web.DTOs.ApprovalQueue;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Casazen.Web.Controllers;

[ApiController]
[Route("api/approval-queue")]
[Authorize(Policy = CasazenPolicies.ApprovalQueueAccess)]
[PrivateAnswer]
public class ApprovalQueueController(
    IApprovalQueueService approvalQueueService,
    ITenantContext tenantContext,
    IHostScopeResolver hostScopeResolver,
    ILogger<ApprovalQueueController> logger,
    IAuthorizationService authorization,
    AppDbContext db) : ControllerBase
{
    [HttpGet]
    [ProducesResponseType(typeof(IEnumerable<ApprovalQueueItemDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<IEnumerable<ApprovalQueueItemDto>>> GetPendingItems(CancellationToken cancellationToken)
    {
        var scope = await ResolveScopeAsync(cancellationToken);
        if (scope.Problem is not null)
            return scope.Problem;

        var (other, marketing) = await PermissionsAsync(scope.Value!.OrgId, cancellationToken);
        if (!other && !marketing) return Forbid();
        var items = (await approvalQueueService.GetPendingAsync(scope.Value, cancellationToken))
            .Where(item => item.ItemType == ApprovalItemTypes.Campaign ? marketing : other);

        return Ok(items.Select(i => ApprovalQueueItemDto.From(i)));
    }

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
        var access = await CheckItemAsync(id, scope.Value!, cancellationToken);
        if (access != null) return access;

        var userId = User.GetUserId();
        if (string.IsNullOrEmpty(userId))
            return UserIdMissing();

        var item = await approvalQueueService.EditAsync(scope.Value!, id, dto.Text, userId, cancellationToken);

        logger.LogInformation("User {UserId} edited approval queue item {ItemId} of type {ItemType}", userId, id, item.ItemType);

        return Ok(ApprovalQueueItemDto.From(item));
    }

    [HttpPost("{id}/approve")]
    [ProducesResponseType(typeof(ApprovalQueueItemDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<ApprovalQueueItemDto>> ApproveItem(Guid id, CancellationToken cancellationToken)
    {
        var scope = await ResolveScopeAsync(cancellationToken);
        if (scope.Problem is not null)
            return scope.Problem;
        var access = await CheckItemAsync(id, scope.Value!, cancellationToken);
        if (access != null) return access;

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
        var access = await CheckItemAsync(id, scope.Value!, cancellationToken);
        if (access != null) return access;

        var userId = User.GetUserId();
        if (string.IsNullOrEmpty(userId))
            return UserIdMissing();

        var item = await approvalQueueService.RejectAsync(scope.Value!, id, userId, dto.Reason, cancellationToken);

        logger.LogInformation("User {UserId} rejected approval queue item {ItemId} of type {ItemType}", userId, id, item.ItemType);

        return Ok(ApprovalQueueItemDto.From(item));
    }

    private async Task<(bool Other, bool Marketing)> PermissionsAsync(Guid orgId, CancellationToken ct)
    {
        bool other = (await authorization.AuthorizeAsync(User, CasazenPolicies.ApprovalDecide)).Succeeded;
        bool marketing = (await authorization.AuthorizeAsync(User, CasazenPolicies.Marketing)).Succeeded;
        if (marketing)
        {
            var org = await db.Orgs.AsNoTracking().SingleOrDefaultAsync(o => o.Id == orgId, ct);
            marketing = org != null && CommercialSignup.HasCrmMarketing(org);
        }
        return (other, marketing);
    }

    private async Task<ActionResult?> CheckItemAsync(Guid id, HostScope scope, CancellationToken ct)
    {
        var (other, marketing) = await PermissionsAsync(scope.OrgId, ct);
        if (!other && !marketing) return Forbid();
        var type = await db.ApprovalQueueItems.Where(i => i.Id == id && i.OrgId == scope.OrgId)
            .Select(i => i.ItemType).SingleOrDefaultAsync(ct);
        if (type != null && !(type == ApprovalItemTypes.Campaign ? marketing : other)) return NotFound();
        return null;
    }

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
