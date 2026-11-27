using Casazen.Core.Authorization;
using Casazen.Core.Entities;

namespace Casazen.Core.Services;

/// <summary>
/// Approval queue for items requiring human decision before taking effect (NF-06), and a decision that <b>executes</b> (E05/F04/US01/T02, NF-68):
/// approving an item runs the action it was waiting for (<see cref="IApprovalOriginAction"/>: a message goes to the guest, a campaign is approved and
/// scheduled), the host may edit the text first, a rejection keeps its reason, and a new item alerts the people who may decide.
/// Covers campaign drafts, the drafts of the assistant for a conversation of the hub, AI-generated texts, routine messages.
/// </summary>
/// <remarks>
/// <para><b>Who and which items.</b> The controller decides that the caller holds <c>approval.decide</c>; every read and every decision here is narrowed to the org
/// of the <see cref="HostScope"/> and, for a caller whose reach is limited to some properties, to the items of those properties
/// (<see cref="ApprovalQueueScope.InScope"/>): an item of another org, or out of the reach, is a <see cref="Exceptions.NotFoundException"/> (404), never a 403.
/// The service checks no role.</para>
/// <para><b>Once.</b> Approving runs the action first and only then marks the item <c>Approved</c>; a second approval of an item already approved changes
/// nothing and sends nothing, and two approvals at the same time reach the guest once (the key of the send is the item's, <see cref="ApprovalQueueRules.SendKeyOf"/>).</para>
/// <para><b>Personal data.</b> The text of an item is never logged: the logs carry ids, the type and the number of characters.</para>
/// </remarks>
public interface IApprovalQueueService
{
    /// <summary>
    /// Submit an item for approval. Creates a pending queue entry with a 7-day expiration, raises ONE "To do" for it (ADR-026: type
    /// <c>MessageToApprove</c>, action <c>ApproveAiDraft</c>, closed when the item is decided or expires) and alerts the users who hold
    /// <c>approval.decide</c> and reach the property, through their notification preferences (<c>AiDraftToApprove</c>): the bell, the push (the route is the
    /// conversation for a draft of a message, <see cref="PushRoutes.Approvals"/> for every other item; the category <c>draft-ready</c> and the field
    /// <c>approvalItemId</c>) and the e-mail they chose, with the quiet hours applied. An item already waiting, or approved in the last <see cref="ApprovalQueueRules.ResubmitWindowDays"/> days, with the same org, type, id and text is the one
    /// that is returned (nothing is created twice when an event is delivered again, even after the host decided) and, while it waits, its alerts are made again, which does no harm: they are idempotent.
    /// A failure of the "To do" or of an alert is logged and handed to a job that tries again; it never undoes the item.
    /// </summary>
    /// <param name="orgId">Organization that owns the item.</param>
    /// <param name="itemType">Type of item (<see cref="ApprovalItemTypes"/>): Campaign, MessageDraft, Routine, AiText.</param>
    /// <param name="itemId">ID of the item requiring approval.</param>
    /// <param name="contentText">Exact text content to approve.</param>
    /// <param name="isAiGenerated">Whether the content was AI-generated.</param>
    /// <param name="contentVersion">Optional version identifier.</param>
    /// <param name="propertyId">
    /// The property the item concerns: it decides who reaches the item and is told about it. <c>null</c> for an item of the org (a campaign) or of a conversation
    /// with no property: only a caller who reaches every property sees it. A property that is not one of the org is ignored (and logged), never trusted.
    /// </param>
    /// <param name="originAction">
    /// The name of what approving does (<see cref="ApprovalOriginActions"/>), when the caller wants it checked: it has to be the one registered for the type
    /// (<see cref="ApprovalOriginActions.None"/> for a type with none), or the call is refused (<see cref="ArgumentException"/>): an item that says it sends a message
    /// and sends nothing is the worst kind of approval.
    /// </param>
    /// <param name="confidenceScore">The score of the assistant for a draft of a message, from 0 to 1.</param>
    /// <param name="situationCode">The situation the assistant recognised; also the rule code of the message when it is sent unedited.</param>
    /// <param name="threadKey">The conversation a draft of a message belongs to (<see cref="MessageThreadKey"/>); required for a <see cref="ApprovalItemTypes.MessageDraft"/>.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The created queue item.</returns>
    Task<ApprovalQueueItem> SubmitForApprovalAsync(
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
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Edit the text of a pending item (the original is kept: <see cref="ApprovalQueueItem.EditedContentText"/>, <see cref="ApprovalQueueItem.EditedByUserId"/>,
    /// <see cref="ApprovalQueueItem.EditedAt"/>). An edit that gives back the original text is no edit: the fields are cleared and the message goes out as the
    /// assistant's. 422 <c>approval_edit_invalid</c> for a text that is empty, a message longer than <see cref="GuestMessageRules.TextMaxLength"/>, or a campaign text
    /// that is not a subject line, a blank line and a body; 404 <c>approval_item_not_found</c>; 409 <c>approval_item_not_pending</c>.
    /// </summary>
    Task<ApprovalQueueItem> EditAsync(
        HostScope scope,
        Guid itemId,
        string? text,
        string userId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Approve a pending item: the action of its type runs with the final text (the edited one when there is one), then the item is <c>Approved</c> and
    /// <see cref="ApprovalQueueItem.OriginActionStatus"/> <c>Executed</c>; an item type with no action is a pure decision (<c>None</c>, <c>NotRequired</c>). When the
    /// action fails the item stays <c>Pending</c> with <c>Failed</c> and the code, and the call is a 409 <c>approval_origin_action_failed</c> (extension
    /// <c>originActionError</c>): the host edits and tries again. An ambiguous send of a message (<c>ManualVerify</c>) counts as executed and says so in
    /// <see cref="ApprovalDecision.ActionState"/>. Closes the "To do" of the item. An item that is already <c>Approved</c> is returned as it is (nothing is
    /// executed again); 404 <c>approval_item_not_found</c>; 409 <c>approval_item_not_pending</c> for a rejected or expired one.
    /// </summary>
    Task<ApprovalDecision> ApproveAsync(
        HostScope scope,
        Guid itemId,
        string userId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Reject a pending item. The reason is mandatory (422 <c>approval_rejection_reason_required</c>, at most
    /// <see cref="ApprovalQueueItem.RejectionReasonMaxLength"/> characters) and is saved; the rejection hook of the type runs (a campaign goes back to a draft); the
    /// "To do" of the item is closed. 404 <c>approval_item_not_found</c>; 409 <c>approval_item_not_pending</c>.
    /// </summary>
    Task<ApprovalQueueItem> RejectAsync(
        HostScope scope,
        Guid itemId,
        string userId,
        string? reason,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Get pending approval items for the org of <paramref name="scope"/>, oldest first, narrowed to the properties the scope reaches: an item with a property
    /// is listed to a caller who reaches it, one with no property to a caller who reaches every property. They carry the confidence score and the thread key.
    /// </summary>
    /// <param name="scope">The caller's reach (its org and its properties).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>List of pending items.</returns>
    Task<List<ApprovalQueueItem>> GetPendingAsync(
        HostScope scope,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Check if content text changed since approval. If changed, return item to pending: the new text replaces the old one, an edit of the old one is dropped,
    /// the action is to run again and the people who may decide are alerted again.
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

/// <summary>
/// The outcome of <see cref="IApprovalQueueService.ApproveAsync"/>: the item as it is now and, for the action that ran, how far it went
/// (<see cref="ApprovalActionResult.State"/>: <c>Sent</c>, <c>Created</c>, <c>WaitingAuthorization</c> or <c>ManualVerify</c> for a message, <c>Scheduled</c> or
/// <c>Approved</c> for a campaign). <paramref name="ActionState"/> is <c>null</c> for a pure decision and for an item that was already approved. The same record tells the
/// <see cref="IApprovalDecisionObserver"/> about every decision (E05/F05/US02/T01): <paramref name="Kind"/> (an approval, a rejection, an edit) and the person who decided
/// (<paramref name="UserId"/>).
/// </summary>
public sealed record ApprovalDecision(
    ApprovalQueueItem Item,
    string? ActionState,
    ApprovalDecisionKind Kind = ApprovalDecisionKind.Approved,
    string? UserId = null);
