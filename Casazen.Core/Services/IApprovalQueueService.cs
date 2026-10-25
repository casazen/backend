using Casazen.Core.Entities;

namespace Casazen.Core.Services;

/// <summary>
/// Approval queue for items requiring human decision before taking effect (NF-06).
/// Covers campaign drafts, AI-generated texts, routine messages.
/// </summary>
public interface IApprovalQueueService
{
    /// <summary>
    /// Submit an item for approval. Creates a pending queue entry with 7-day expiration.
    /// Sends in-app notification to users with approval permission.
    /// </summary>
    /// <param name="orgId">Organization that owns the item.</param>
    /// <param name="itemType">Type of item: Campaign, Routine, AiText.</param>
    /// <param name="itemId">ID of the item requiring approval.</param>
    /// <param name="contentText">Exact text content to approve.</param>
    /// <param name="isAiGenerated">Whether the content was AI-generated.</param>
    /// <param name="contentVersion">Optional version identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The created queue item.</returns>
    Task<ApprovalQueueItem> SubmitForApprovalAsync(
        Guid orgId,
        string itemType,
        Guid itemId,
        string contentText,
        bool isAiGenerated,
        string? contentVersion = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Approve a pending item. Logs exact text, user ID, and timestamp.
    /// </summary>
    /// <param name="itemId">Queue item ID.</param>
    /// <param name="userId">User approving the item (Auth0 subject).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The approved item, or null if not found or not pending.</returns>
    Task<ApprovalQueueItem?> ApproveAsync(
        Guid itemId,
        string userId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Reject a pending item.
    /// </summary>
    /// <param name="itemId">Queue item ID.</param>
    /// <param name="userId">User rejecting the item (Auth0 subject).</param>
    /// <param name="reason">Reason for rejection.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The rejected item, or null if not found or not pending.</returns>
    Task<ApprovalQueueItem?> RejectAsync(
        Guid itemId,
        string userId,
        string reason,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Get pending approval items for an organization.
    /// </summary>
    /// <param name="orgId">Organization ID.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>List of pending items.</returns>
    Task<List<ApprovalQueueItem>> GetPendingAsync(
        Guid orgId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Check if content text changed since approval. If changed, return item to pending.
    /// </summary>
    /// <param name="itemType">Type of item.</param>
    /// <param name="itemId">ID of the item.</param>
    /// <param name="currentContentText">Current content text.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True if content changed and item was returned to pending.</returns>
    Task<bool> ReturnToPendingIfChangedAsync(
        string itemType,
        Guid itemId,
        string currentContentText,
        CancellationToken cancellationToken = default);
}
