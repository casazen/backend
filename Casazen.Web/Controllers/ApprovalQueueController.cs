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
/// Approval queue for items requiring human decision before taking effect (NF-06).
/// </summary>
[ApiController]
[Route("api/approval-queue")]
public class ApprovalQueueController(
    IApprovalQueueService approvalQueueService,
    ITenantContext tenantContext,
    ILogger<ApprovalQueueController> logger) : ControllerBase
{
    /// <summary>
    /// Get pending approval items for the caller's organization.
    /// </summary>
    [HttpGet]
    [Authorize(Policy = CasazenPolicies.PropertyRead)]
    public async Task<ActionResult<IEnumerable<ApprovalQueueItemDto>>> GetPendingItems()
    {
        var orgId = tenantContext.OrgId ?? Guid.Empty;
        if (orgId == Guid.Empty)
        {
            return this.ApiProblem(
                StatusCodes.Status401Unauthorized,
                ProblemCodes.Unauthorized,
                "OrgContextRequired");
        }

        var items = await approvalQueueService.GetPendingAsync(orgId);

        var dtos = items.Select(i => new ApprovalQueueItemDto
        {
            Id = i.Id,
            ItemType = i.ItemType,
            ItemId = i.ItemId,
            ContentText = i.ContentText,
            ContentVersion = i.ContentVersion,
            IsAiGenerated = i.IsAiGenerated,
            Status = i.Status.ToString(),
            CreatedAt = i.CreatedAt,
            ExpiresAt = i.ExpiresAt,
        });

        return Ok(dtos);
    }

    /// <summary>
    /// Approve a pending approval queue item.
    /// </summary>
    [HttpPost("{id}/approve")]
    [Authorize(Policy = CasazenPolicies.PropertyWrite)]
    public async Task<ActionResult<ApprovalQueueItemDto>> ApproveItem(Guid id)
    {
        var userId = User.GetUserId();
        if (string.IsNullOrEmpty(userId))
        {
            return this.ApiProblem(
                StatusCodes.Status401Unauthorized,
                ProblemCodes.Unauthorized,
                "UserIdMissing");
        }

        var item = await approvalQueueService.ApproveAsync(id, userId);
        if (item == null)
        {
            return this.ApiProblem(
                StatusCodes.Status404NotFound,
                ProblemCodes.NotFound,
                "ApprovalItemNotFound");
        }

        logger.LogInformation(
            "User {UserId} approved approval queue item {ItemId} of type {ItemType}",
            userId,
            id,
            item.ItemType);

        return Ok(new ApprovalQueueItemDto
        {
            Id = item.Id,
            ItemType = item.ItemType,
            ItemId = item.ItemId,
            ContentText = item.ContentText,
            ContentVersion = item.ContentVersion,
            IsAiGenerated = item.IsAiGenerated,
            Status = item.Status.ToString(),
            ApprovedByUserId = item.ApprovedByUserId,
            ApprovedAt = item.ApprovedAt,
            CreatedAt = item.CreatedAt,
            ExpiresAt = item.ExpiresAt,
        });
    }

    /// <summary>
    /// Reject a pending approval queue item.
    /// </summary>
    [HttpPost("{id}/reject")]
    [Authorize(Policy = CasazenPolicies.PropertyWrite)]
    public async Task<ActionResult<ApprovalQueueItemDto>> RejectItem(
        Guid id,
        [FromBody] RejectApprovalItemDto dto)
    {
        var userId = User.GetUserId();
        if (string.IsNullOrEmpty(userId))
        {
            return this.ApiProblem(
                StatusCodes.Status401Unauthorized,
                ProblemCodes.Unauthorized,
                "UserIdMissing");
        }

        var item = await approvalQueueService.RejectAsync(id, userId, dto.Reason ?? "No reason provided");
        if (item == null)
        {
            return this.ApiProblem(
                StatusCodes.Status404NotFound,
                ProblemCodes.NotFound,
                "ApprovalItemNotFound");
        }

        logger.LogInformation(
            "User {UserId} rejected approval queue item {ItemId} of type {ItemType}. Reason: {Reason}",
            userId,
            id,
            item.ItemType,
            dto.Reason);

        return Ok(new ApprovalQueueItemDto
        {
            Id = item.Id,
            ItemType = item.ItemType,
            ItemId = item.ItemId,
            ContentText = item.ContentText,
            ContentVersion = item.ContentVersion,
            IsAiGenerated = item.IsAiGenerated,
            Status = item.Status.ToString(),
            ApprovedByUserId = item.ApprovedByUserId,
            ApprovedAt = item.ApprovedAt,
            CreatedAt = item.CreatedAt,
            ExpiresAt = item.ExpiresAt,
        });
    }
}
