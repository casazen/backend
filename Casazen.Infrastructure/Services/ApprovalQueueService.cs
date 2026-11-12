using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Services;

/// <summary>
/// Approval queue service for items requiring human decision before taking effect (NF-06), and a decision that executes what the item was waiting for (E05/F04/US01/T02,
/// NF-68): see <see cref="IApprovalQueueService"/>. The action of a type is found in <see cref="IApprovalOriginActionRegistry"/>; the "To do" and the alerts of a new item are
/// <see cref="ApprovalAlertDelivery"/>'s.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><b>Approval order.</b> The action runs first, with the final text (the edited one when there is one); only when it succeeds does the item become <c>Approved</c> and
/// <c>Executed</c>, and that save, and the closing of the "To do", do not depend on the request token any more (a message that went out must be recorded). When the action
/// fails the item stays <c>Pending</c>, <c>Failed</c> with the code, and the answer is a 409 <c>approval_origin_action_failed</c>; an exception nobody foresaw is recorded as
/// <see cref="ApprovalQueueRules.UnexpectedErrorCode"/> and let through.</item>
/// <item><b>Reach.</b> Every item is read in the org of the scope and through <see cref="ApprovalQueueScope.InScope"/>: an item of a property the caller does not reach is a 404.</item>
/// <item><b>Nothing of the text in a log</b>: ids, the type and a number of characters.</item>
/// </list>
/// </remarks>
public class ApprovalQueueService(
    AppDbContext db,
    TimeProvider timeProvider,
    IApprovalOriginActionRegistry actions,
    IInternalActivityService activities,
    ApprovalAlertDelivery alerts,
    ILogger<ApprovalQueueService> logger) : IApprovalQueueService
{
    private const int ExpirationDays = ApprovalQueueRules.ExpirationDays;
    private const int ContentVersionMaxLength = 100;

    /// <inheritdoc/>
    public async Task<ApprovalQueueItem> SubmitForApprovalAsync(
        Guid orgId,
        string itemType,
        Guid itemId,
        string contentText,
        bool isAiGenerated,
        string? contentVersion = null,
        Guid? propertyId = null,
        string? originAction = null,
        decimal? confidenceScore = null,
        string? situationCode = null,
        string? threadKey = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(itemType);
        ArgumentException.ThrowIfNullOrWhiteSpace(contentText);
        if (itemType.Length > 50)
            throw new ArgumentException("An item type is at most 50 characters.", nameof(itemType));

        if (contentVersion is { Length: > ContentVersionMaxLength })
            throw new ArgumentException($"A content version is at most {ContentVersionMaxLength} characters.", nameof(contentVersion));

        if (confidenceScore is < 0m or > 1m)
            throw new ArgumentOutOfRangeException(nameof(confidenceScore), confidenceScore, "A confidence score is from 0 to 1.");

        if (situationCode is not null && (string.IsNullOrWhiteSpace(situationCode) || situationCode.Length > ApprovalQueueItem.SituationCodeMaxLength))
            throw new ArgumentException($"A situation code is a code of at most {ApprovalQueueItem.SituationCodeMaxLength} characters.", nameof(situationCode));

        string? thread = null;
        if (threadKey is not null)
        {
            thread = MessageThreadKey.TryParse(threadKey, out var parsed)
                ? parsed.ToString()
                : throw new ArgumentException("A thread key is 'ota:' or 'direct:' followed by a GUID.", nameof(threadKey));
        }

        var isMessageDraft = string.Equals(itemType, ApprovalItemTypes.MessageDraft, StringComparison.Ordinal);
        if (isMessageDraft && thread is null)
            throw new ArgumentException("The draft of a message names the conversation it belongs to.", nameof(threadKey));

        // What approving does is what the registry says for the type: an item that says it sends a message and has no action that sends it would be approved and do nothing.
        var action = actions.Find(itemType);
        var origin = action?.OriginAction ?? ApprovalOriginActions.None;
        if (originAction is not null && !string.Equals(originAction, origin, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"An item of type '{itemType}' runs '{origin}' when it is approved, not '{originAction}'.",
                nameof(originAction));
        }

        // A property that is not one of the org is never trusted: the item is then the org's, which only a caller who reaches every property sees.
        if (propertyId is { } claimed && !await db.Properties.AsNoTracking().AnyAsync(p => p.Id == claimed && p.OrgId == orgId, cancellationToken))
        {
            logger.LogWarning("An item for approval of org {OrgId} names a property that is not one of the org: it is stored with no property", orgId);
            propertyId = null;
        }

        // The same item delivered again (an event of the outbox, a retry): the one that waits is the one that is returned, and its alerts are made again (idempotent).
        var waiting = await db.ApprovalQueueItems
            .Where(i => i.OrgId == orgId && i.ItemType == itemType && i.ItemId == itemId && i.Status == ApprovalStatus.Pending)
            .ToListAsync(cancellationToken);
        if (waiting.FirstOrDefault(i => string.Equals(i.ContentText, contentText, StringComparison.Ordinal)) is { } existing)
        {
            logger.LogInformation(
                "Item {ItemId} of type {ItemType} was already waiting for approval in org {OrgId} (queue item {QueueItemId}): nothing is created again",
                itemId,
                itemType,
                orgId,
                existing.Id);
            await alerts.RaiseOrRetryAsync(new ApprovalAlert(orgId, existing.Id, existing.CreatedAt), cancellationToken);
            return existing;
        }

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
            PropertyId = propertyId,
            OriginAction = origin,
            OriginActionStatus = action is null ? ApprovalOriginActionStatus.NotRequired : ApprovalOriginActionStatus.Pending,
            ConfidenceScore = confidenceScore,
            SituationCode = situationCode,
            ThreadKey = thread,
        };

        db.ApprovalQueueItems.Add(item);
        await db.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "Item {ItemId} of type {ItemType} submitted for approval in org {OrgId} (queue item {QueueItemId}), origin action {OriginAction}, expires at {ExpiresAt}",
            itemId,
            itemType,
            orgId,
            item.Id,
            origin,
            expiresAt);

        // The item is saved: from here a failure of the "To do" or of an alert is logged and retried by a job, and never undoes it.
        await alerts.RaiseOrRetryAsync(new ApprovalAlert(orgId, item.Id, item.CreatedAt), cancellationToken);

        return item;
    }

    /// <inheritdoc/>
    public async Task<ApprovalQueueItem> EditAsync(
        HostScope scope,
        Guid itemId,
        string? text,
        string userId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        UserIdOf(userId);

        var item = await FindInScopeAsync(scope, itemId, cancellationToken);
        RequirePending(item);

        var edited = ApprovalQueueRules.NormalizeEditedText(item.ItemType, text)
                     ?? throw new DomainRuleException(ApprovalQueueRules.EditInvalidCode, "ApprovalEditInvalid");

        // The original text is never overwritten. An edit that gives the original back is no edit: the fields are cleared, so the message goes out as it was written.
        var original = ApprovalQueueRules.NormalizeEditedText(item.ItemType, item.ContentText);
        if (original is not null && string.Equals(edited, original, StringComparison.Ordinal))
        {
            item.EditedContentText = null;
            item.EditedByUserId = null;
            item.EditedAt = null;
        }
        else
        {
            item.EditedContentText = edited;
            item.EditedByUserId = userId;
            item.EditedAt = timeProvider.GetUtcNow().UtcDateTime;
        }

        await db.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "Item {QueueItemId} of type {ItemType} edited by a user in org {OrgId}: {Length} characters, {State}",
            item.Id,
            item.ItemType,
            item.OrgId,
            edited.Length,
            item.EditedContentText is null ? "back to the original" : "edited");

        return item;
    }

    /// <inheritdoc/>
    public async Task<ApprovalDecision> ApproveAsync(
        HostScope scope,
        Guid itemId,
        string userId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        UserIdOf(userId);

        var item = await FindInScopeAsync(scope, itemId, cancellationToken);

        // A second approval (a double click, a retry after an answer that never arrived) changes nothing and sends nothing; it only makes sure the "To do" is closed.
        if (item.Status == ApprovalStatus.Approved)
        {
            await CloseTodoAsync(item, userId, CancellationToken.None);
            return new ApprovalDecision(item, null);
        }

        RequirePending(item);

        var finalText = item.EditedContentText ?? item.ContentText;
        var action = actions.Find(item.ItemType);
        string? actionState = null;

        if (action is not null)
        {
            ApprovalActionResult result;
            try
            {
                result = await action.ExecuteAsync(item, finalText, userId, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Approval of item {QueueItemId} of type {ItemType} in org {OrgId} raised an error", item.Id, item.ItemType, item.OrgId);
                await RecordFailureAsync(item, action, ApprovalQueueRules.UnexpectedErrorCode);
                throw;
            }

            if (!result.Succeeded)
            {
                var code = string.IsNullOrWhiteSpace(result.ErrorCode) ? ApprovalQueueRules.UnexpectedErrorCode : result.ErrorCode;
                logger.LogWarning(
                    "Approval of item {QueueItemId} of type {ItemType} in org {OrgId}: the action failed ({Code}), the item stays pending",
                    item.Id,
                    item.ItemType,
                    item.OrgId,
                    code);
                await RecordFailureAsync(item, action, code);
                throw new DomainConflictException(ApprovalQueueRules.OriginActionFailedCode, "ApprovalOriginActionFailed")
                {
                    Extensions = new Dictionary<string, object?> { ["originActionError"] = item.OriginActionError },
                };
            }

            actionState = result.State;
        }

        var now = timeProvider.GetUtcNow().UtcDateTime;
        item.Status = ApprovalStatus.Approved;
        item.ApprovedByUserId = userId;
        item.ApprovedAt = now;
        if (action is not null)
        {
            item.OriginAction = action.OriginAction;
            item.OriginActionStatus = ApprovalOriginActionStatus.Executed;
            item.OriginActionExecutedAt = now;
            item.OriginActionError = null;
        }
        else
        {
            item.OriginAction = ApprovalOriginActions.None;
            item.OriginActionStatus = ApprovalOriginActionStatus.NotRequired;
        }

        // The action has run (a message may be on its way to the guest): the decision is recorded whatever happens to the request.
        await db.SaveChangesAsync(CancellationToken.None);

        logger.LogInformation(
            "Item {QueueItemId} of type {ItemType} approved by a user in org {OrgId} at {ApprovedAt}: action {OriginAction} {ActionState}, {Length} characters",
            item.Id,
            item.ItemType,
            item.OrgId,
            now,
            item.OriginAction,
            actionState ?? "(none)",
            finalText.Length);

        await CloseTodoAsync(item, userId, CancellationToken.None);
        return new ApprovalDecision(item, actionState);
    }

    /// <inheritdoc/>
    public async Task<ApprovalQueueItem> RejectAsync(
        HostScope scope,
        Guid itemId,
        string userId,
        string? reason,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        UserIdOf(userId);

        var item = await FindInScopeAsync(scope, itemId, cancellationToken);
        RequirePending(item);

        var cleanReason = ApprovalQueueRules.NormalizeRejectionReason(reason)
                          ?? throw new DomainRuleException(ApprovalQueueRules.RejectionReasonRequiredCode, "ApprovalRejectionReasonRequired");

        // What the rejection undoes (a campaign goes back to a draft) comes first and is safe to repeat: a failure leaves the item pending, to be rejected again.
        if (actions.Find(item.ItemType) is { } action)
            await action.OnRejectedAsync(item, cleanReason, userId, cancellationToken);

        var now = timeProvider.GetUtcNow().UtcDateTime;
        item.Status = ApprovalStatus.Rejected;
        item.ApprovedByUserId = userId;
        item.ApprovedAt = now;
        item.RejectionReason = cleanReason;
        await db.SaveChangesAsync(cancellationToken);

        // The reason is the host's own words, which may name a guest: only its length is logged.
        logger.LogInformation(
            "Item {QueueItemId} of type {ItemType} rejected by a user in org {OrgId} at {RejectedAt}, reason of {Length} characters",
            item.Id,
            item.ItemType,
            item.OrgId,
            now,
            cleanReason.Length);

        await CloseTodoAsync(item, userId, cancellationToken);
        return item;
    }

    /// <inheritdoc/>
    public async Task<List<ApprovalQueueItem>> GetPendingAsync(
        HostScope scope,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);

        return await db.ApprovalQueueItems
            .Where(i => i.OrgId == scope.OrgId && i.Status == ApprovalStatus.Pending)
            .InScope(scope, db.Properties)
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

            // The new text has to be decided again, with its action: the edit of the old text, the result of the old run and the old "To do" (closed by the approval) go.
            item.EditedContentText = null;
            item.EditedByUserId = null;
            item.EditedAt = null;
            item.RejectionReason = null;
            item.OriginActionStatus = actions.Find(item.ItemType) is null ? ApprovalOriginActionStatus.NotRequired : ApprovalOriginActionStatus.Pending;
            item.OriginActionError = null;
            item.OriginActionExecutedAt = null;
            item.ActivityId = null;

            await db.SaveChangesAsync(cancellationToken);

            logger.LogInformation(
                "Item {ItemId} of type {ItemType} returned to pending due to content change",
                itemId,
                itemType);

            // The people who may decide are told again: a new alert, with its own keys.
            await alerts.RaiseOrRetryAsync(new ApprovalAlert(item.OrgId, item.Id, now), cancellationToken);

            return true;
        }

        return false;
    }

    // ─── Helpers ───

    /// <summary>The item in the org and the reach of the scope, tracked; a 404 for any other (another org, another property, none).</summary>
    private async Task<ApprovalQueueItem> FindInScopeAsync(HostScope scope, Guid itemId, CancellationToken ct) =>
        await db.ApprovalQueueItems
            .Where(i => i.OrgId == scope.OrgId && i.Id == itemId)
            .InScope(scope, db.Properties)
            .FirstOrDefaultAsync(ct)
        ?? throw new NotFoundException("The approval item was not found or is not within reach")
        {
            Code = ApprovalQueueRules.NotFoundCode,
            MessageKey = "ApprovalItemNotFound",
        };

    private static void RequirePending(ApprovalQueueItem item)
    {
        if (item.Status != ApprovalStatus.Pending)
            throw new DomainConflictException(ApprovalQueueRules.NotPendingCode, "ApprovalItemNotPending");
    }

    private static string UserIdOf(string userId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        return userId.Length <= TodoLimits.UserIdMaxLength
            ? userId
            : throw new ArgumentException($"A user id is at most {TodoLimits.UserIdMaxLength} characters.", nameof(userId));
    }

    /// <summary>Keeps the failure of the action in the item, which stays pending: the code of why (never a sentence), for the host to edit and try again.</summary>
    private async Task RecordFailureAsync(ApprovalQueueItem item, IApprovalOriginAction action, string code)
    {
        item.OriginAction = action.OriginAction;
        item.OriginActionStatus = ApprovalOriginActionStatus.Failed;
        item.OriginActionError = code.Length <= ApprovalQueueItem.OriginActionErrorMaxLength ? code : code[..ApprovalQueueItem.OriginActionErrorMaxLength];
        await db.SaveChangesAsync(CancellationToken.None);
    }

    /// <summary>
    /// Closes the "To do" of a decided item (ADR-026). A "To do" that is not there any more (the host closed it, or it was never raised) is not an error; any other failure is, and the
    /// decision that was already saved is found by the next approval, which closes it again.
    /// </summary>
    private async Task CloseTodoAsync(ApprovalQueueItem item, string closedBy, CancellationToken ct)
    {
        if (item.ActivityId is not { } activityId)
            return;

        try
        {
            await activities.CloseAsync(activityId, closedBy, ct);
        }
        catch (NotFoundException)
        {
            logger.LogInformation("The To do of item {QueueItemId} of org {OrgId} was not there any more: nothing to close", item.Id, item.OrgId);
        }
    }
}
