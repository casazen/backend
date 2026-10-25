using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Services;

/// <summary>
/// Approval queue service for items requiring human decision before taking effect (NF-06).
/// </summary>
public class ApprovalQueueService(
    AppDbContext db,
    TimeProvider timeProvider,
    ILogger<ApprovalQueueService> logger) : IApprovalQueueService
{
    private const int ExpirationDays = 7;

    /// <inheritdoc/>
    public async Task<ApprovalQueueItem> SubmitForApprovalAsync(
        Guid orgId,
        string itemType,
        Guid itemId,
        string contentText,
        bool isAiGenerated,
        string? contentVersion = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(itemType);
        ArgumentException.ThrowIfNullOrWhiteSpace(contentText);

        var now = timeProvider.GetUtcNow().UtcDateTime;
        var expiresAt = now.AddDays(ExpirationDays);

        var item = new ApprovalQueueItem
        {
            OrgId = orgId,
            ItemType = itemType,
            ItemId = itemId,
            ContentText = contentText,
            ContentVersion = contentVersion,
            IsAiGenerated = isAiGenerated,
            Status = ApprovalStatus.Pending,
            CreatedAt = now,
            ExpiresAt = expiresAt,
        };

        db.ApprovalQueueItems.Add(item);
        await db.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "Item {ItemId} of type {ItemType} submitted for approval in org {OrgId}, expires at {ExpiresAt}",
            itemId,
            itemType,
            orgId,
            expiresAt);

        // Send in-app notification to users with approval permission
        await SendApprovalNotificationAsync(orgId, item.Id, itemType, cancellationToken);

        return item;
    }

    /// <inheritdoc/>
    public async Task<ApprovalQueueItem?> ApproveAsync(
        Guid itemId,
        string userId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);

        var item = await db.ApprovalQueueItems
            .FirstOrDefaultAsync(i => i.Id == itemId, cancellationToken);

        if (item == null || item.Status != ApprovalStatus.Pending)
        {
            return null;
        }

        var now = timeProvider.GetUtcNow().UtcDateTime;

        item.Status = ApprovalStatus.Approved;
        item.ApprovedByUserId = userId;
        item.ApprovedAt = now;

        await db.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "Item {ItemId} of type {ItemType} approved by user {UserId} at {ApprovedAt}. Content: {ContentLength} chars",
            itemId,
            item.ItemType,
            userId,
            now,
            item.ContentText.Length);

        return item;
    }

    /// <inheritdoc/>
    public async Task<ApprovalQueueItem?> RejectAsync(
        Guid itemId,
        string userId,
        string reason,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);

        var item = await db.ApprovalQueueItems
            .FirstOrDefaultAsync(i => i.Id == itemId, cancellationToken);

        if (item == null || item.Status != ApprovalStatus.Pending)
        {
            return null;
        }

        var now = timeProvider.GetUtcNow().UtcDateTime;

        item.Status = ApprovalStatus.Rejected;
        item.ApprovedByUserId = userId;
        item.ApprovedAt = now;

        await db.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "Item {ItemId} of type {ItemType} rejected by user {UserId}. Reason: {Reason}",
            itemId,
            item.ItemType,
            userId,
            reason);

        return item;
    }

    /// <inheritdoc/>
    public async Task<List<ApprovalQueueItem>> GetPendingAsync(
        Guid orgId,
        CancellationToken cancellationToken = default)
    {
        return await db.ApprovalQueueItems
            .Where(i => i.OrgId == orgId && i.Status == ApprovalStatus.Pending)
            .OrderBy(i => i.CreatedAt)
            .ToListAsync(cancellationToken);
    }

    /// <inheritdoc/>
    public async Task<bool> ReturnToPendingIfChangedAsync(
        string itemType,
        Guid itemId,
        string currentContentText,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(itemType);
        ArgumentException.ThrowIfNullOrWhiteSpace(currentContentText);

        var item = await db.ApprovalQueueItems
            .Where(i => i.ItemType == itemType && i.ItemId == itemId)
            .OrderByDescending(i => i.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken);

        if (item == null)
        {
            return false;
        }

        // Only return to pending if currently approved and content changed
        if (item.Status == ApprovalStatus.Approved && item.ContentText != currentContentText)
        {
            var now = timeProvider.GetUtcNow().UtcDateTime;

            item.Status = ApprovalStatus.Pending;
            item.ApprovedByUserId = null;
            item.ApprovedAt = null;
            item.ContentText = currentContentText;
            item.ExpiresAt = now.AddDays(ExpirationDays);

            await db.SaveChangesAsync(cancellationToken);

            logger.LogInformation(
                "Item {ItemId} of type {ItemType} returned to pending due to content change",
                itemId,
                itemType);

            // Send notification about the change
            await SendApprovalNotificationAsync(item.OrgId, item.Id, itemType, cancellationToken);

            return true;
        }

        return false;
    }

    /// <summary>
    /// Send in-app notification to users with approval permission.
    /// For now, creates a generic notification. In a full implementation, this would
    /// integrate with the permission system to find users with approval permission.
    /// </summary>
    private async Task SendApprovalNotificationAsync(
        Guid orgId,
        Guid queueItemId,
        string itemType,
        CancellationToken cancellationToken)
    {
        // Get active org members (simplified - in reality would check approval permission)
        var orgMemberUserIds = await db.OrgMembers
            .Where(m => m.OrgId == orgId && m.Status == OrgMemberStatus.Active)
            .Select(m => m.UserId)
            .ToListAsync(cancellationToken);

        if (orgMemberUserIds.Count == 0)
        {
            logger.LogWarning(
                "No active org members found for org {OrgId} to notify about approval queue item",
                orgId);
            return;
        }

        var now = timeProvider.GetUtcNow().UtcDateTime;
        var deliveryKey = $"approval-queue:{queueItemId}";

        // Create in-app notifications for all org members
        foreach (var userId in orgMemberUserIds)
        {
            try
            {
                db.InAppNotifications.Add(new InAppNotification
                {
                    OrgId = orgId,
                    UserId = userId,
                    Type = "approval-queue-item",
                    EntityId = queueItemId,
                    DeliveryKey = deliveryKey,
                    CreatedAt = now,
                });

                await db.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateException ex) when (ex.InnerException is Npgsql.PostgresException pgEx && pgEx.SqlState == "23505")
            {
                // Duplicate key - notification already exists for this user
                db.ChangeTracker.Clear();
                logger.LogDebug(
                    "In-app notification already exists for user {UserId} and delivery key {DeliveryKey}",
                    userId,
                    deliveryKey);
            }
        }

        logger.LogInformation(
            "Sent in-app notifications to {Count} users for approval queue item {QueueItemId}",
            orgMemberUserIds.Count,
            queueItemId);
    }
}
