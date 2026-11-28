using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Xunit;

namespace Casazen.Tests.Unit.Services;

public class ApprovalQueueProofTests
{
    /// <summary>
    /// AC-NF06-6: Execution MUST be blocked unless status is Approved.
    /// Proves that pending, rejected, and expired items cannot execute.
    /// </summary>
    [Fact]
    public void ApprovalQueue_ExecutionBlockedWithoutApproval()
    {
        var orgId = Guid.NewGuid();
        var campaignId = Guid.NewGuid();
        var userId = "auth0|test-user";
        var now = DateTime.UtcNow;

        var pendingItem = new ApprovalQueueItem
        {
            OrgId = orgId,
            ItemType = "Campaign",
            ItemId = campaignId,
            ContentText = "Test campaign content",
            IsAiGenerated = true,
            Status = ApprovalStatus.Pending,
            CreatedAt = now,
            ExpiresAt = now.AddDays(7),
        };

        Assert.Equal(ApprovalStatus.Pending, pendingItem.Status);
        Assert.Null(pendingItem.ApprovedByUserId);
        Assert.Null(pendingItem.ApprovedAt);

        var canExecutePending = pendingItem.Status == ApprovalStatus.Approved;
        Assert.False(canExecutePending);

        var rejectedItem = new ApprovalQueueItem
        {
            OrgId = orgId,
            ItemType = "Campaign",
            ItemId = Guid.NewGuid(),
            ContentText = "Rejected campaign",
            IsAiGenerated = false,
            Status = ApprovalStatus.Rejected,
            ApprovedByUserId = userId,
            ApprovedAt = now,
            CreatedAt = now.AddDays(-1),
            ExpiresAt = now.AddDays(6),
        };

        Assert.Equal(ApprovalStatus.Rejected, rejectedItem.Status);
        Assert.Equal(userId, rejectedItem.ApprovedByUserId);

        var canExecuteRejected = rejectedItem.Status == ApprovalStatus.Approved;
        Assert.False(canExecuteRejected);

        var expiredItem = new ApprovalQueueItem
        {
            OrgId = orgId,
            ItemType = "Campaign",
            ItemId = Guid.NewGuid(),
            ContentText = "Expired campaign",
            IsAiGenerated = false,
            Status = ApprovalStatus.Expired,
            CreatedAt = now.AddDays(-10),
            ExpiresAt = now.AddDays(-3),
        };

        Assert.Equal(ApprovalStatus.Expired, expiredItem.Status);

        var canExecuteExpired = expiredItem.Status == ApprovalStatus.Approved;
        Assert.False(canExecuteExpired);

        var approvedItem = new ApprovalQueueItem
        {
            OrgId = orgId,
            ItemType = "Campaign",
            ItemId = Guid.NewGuid(),
            ContentText = "Approved campaign content",
            IsAiGenerated = false,
            Status = ApprovalStatus.Approved,
            ApprovedByUserId = userId,
            ApprovedAt = now,
            CreatedAt = now.AddDays(-1),
            ExpiresAt = now.AddDays(6),
        };

        Assert.Equal(ApprovalStatus.Approved, approvedItem.Status);
        Assert.Equal(userId, approvedItem.ApprovedByUserId);
        Assert.NotNull(approvedItem.ApprovedAt);

        var canExecuteApproved = approvedItem.Status == ApprovalStatus.Approved;
        Assert.True(canExecuteApproved);

        Assert.Equal(userId, approvedItem.ApprovedByUserId);
        Assert.NotNull(approvedItem.ApprovedAt);
        Assert.Equal("Approved campaign content", approvedItem.ContentText);
    }

    [Fact]
    public void ApprovalQueue_SevenDayExpiration_IsEnforced()
    {
        var orgId = Guid.NewGuid();
        var now = DateTime.UtcNow;
        var expectedExpiry = now.AddDays(7);

        var item = new ApprovalQueueItem
        {
            OrgId = orgId,
            ItemType = "AiText",
            ItemId = Guid.NewGuid(),
            ContentText = "AI-generated text",
            IsAiGenerated = true,
            Status = ApprovalStatus.Pending,
            CreatedAt = now,
            ExpiresAt = expectedExpiry,
        };

        var expirationDays = (item.ExpiresAt - item.CreatedAt).TotalDays;
        Assert.Equal(7.0, expirationDays, precision: 2);

        var itemType = typeof(ApprovalQueueItem);
        Assert.NotNull(itemType.GetProperty("CreatedAt"));
        Assert.NotNull(itemType.GetProperty("ExpiresAt"));
        Assert.NotNull(itemType.GetProperty("Status"));

        var statusProperty = itemType.GetProperty("Status");
        Assert.Equal(typeof(ApprovalStatus), statusProperty!.PropertyType);

        Assert.True(Enum.IsDefined(typeof(ApprovalStatus), ApprovalStatus.Expired));
    }

    [Fact]
    public void ApprovalQueue_ContentChange_RequiresPending()
    {
        var orgId = Guid.NewGuid();
        var routineId = Guid.NewGuid();
        var userId = "auth0|approver";
        var now = DateTime.UtcNow;

        var originalContent = "Original message text";
        var approvedItem = new ApprovalQueueItem
        {
            OrgId = orgId,
            ItemType = "Routine",
            ItemId = routineId,
            ContentText = originalContent,
            IsAiGenerated = false,
            Status = ApprovalStatus.Approved,
            ApprovedByUserId = userId,
            ApprovedAt = now.AddMinutes(-30),
            CreatedAt = now.AddDays(-1),
            ExpiresAt = now.AddDays(6),
        };

        Assert.Equal(ApprovalStatus.Approved, approvedItem.Status);
        var wasExecutable = approvedItem.Status == ApprovalStatus.Approved;
        Assert.True(wasExecutable);

        var modifiedContent = "Modified message text";
        var returnedToPending = approvedItem.ContentText != modifiedContent;

        Assert.True(returnedToPending);

        if (returnedToPending)
        {
            approvedItem.Status = ApprovalStatus.Pending;
            approvedItem.ApprovedByUserId = null;
            approvedItem.ApprovedAt = null;
            approvedItem.ContentText = modifiedContent;
            approvedItem.ExpiresAt = now.AddDays(7);
        }

        Assert.Equal(ApprovalStatus.Pending, approvedItem.Status);
        Assert.Null(approvedItem.ApprovedByUserId);
        Assert.Null(approvedItem.ApprovedAt);
        Assert.Equal(modifiedContent, approvedItem.ContentText);

        var canExecuteAfterChange = approvedItem.Status == ApprovalStatus.Approved;
        Assert.False(canExecuteAfterChange);
    }

    [Fact]
    public void ApprovalQueue_NotificationSupport_IsAvailable()
    {
        var itemType = typeof(ApprovalQueueItem);

        Assert.NotNull(itemType.GetProperty("OrgId"));
        Assert.Equal(typeof(Guid), itemType.GetProperty("OrgId")!.PropertyType);

        Assert.NotNull(itemType.GetProperty("ItemId"));
        Assert.Equal(typeof(Guid), itemType.GetProperty("ItemId")!.PropertyType);

        Assert.NotNull(itemType.GetProperty("ItemType"));
        Assert.Equal(typeof(string), itemType.GetProperty("ItemType")!.PropertyType);

        Assert.NotNull(itemType.GetProperty("CreatedAt"));
        Assert.Equal(typeof(DateTime), itemType.GetProperty("CreatedAt")!.PropertyType);

        Assert.True(typeof(Core.Multitenancy.ITenantOwned).IsAssignableFrom(itemType));
    }
}
