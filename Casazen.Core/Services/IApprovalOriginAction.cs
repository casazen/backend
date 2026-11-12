using Casazen.Core.Entities;

namespace Casazen.Core.Services;

/// <summary>
/// What approving an item of the queue does, for one <see cref="ItemType"/> (E05/F04/US01/T02, NF-68): it sends the message, approves and
/// schedules the campaign. An item type with <b>no</b> registered action (a routine in test mode, a text of the AI, a type a later epic adds) is a pure
/// decision: approving it only changes its status (<see cref="ApprovalOriginActions.None"/>, <see cref="Entities.Enums.ApprovalOriginActionStatus.NotRequired"/>).
/// The actions are registered by the extension class of the feature that owns the type (<c>AddApprovalQueueActions</c> for the two of the queue; the
/// assistant, the reviews and the campaigns of E07 register their own from theirs), and found through <see cref="IApprovalOriginActionRegistry"/>.
/// </summary>
/// <remarks>
/// <para><b>Who may decide</b> is not asked here: <c>IApprovalQueueService</c> checks that the caller holds the permission (the controller) and reaches
/// the property of the item (the scope) before it calls the action, which runs for the whole org of the item and never checks a role.</para>
/// <para><b>Once.</b> An action can be called again for the same item (a double click, a retry after a failure, a request that timed out after the send):
/// it must be safe to repeat. The message uses the key <see cref="ApprovalQueueRules.SendKeyOf"/>, so a second call reaches the guest once.</para>
/// <para><b>Failures are codes.</b> An expected failure (the conversation is closed, the channel refuses the text) is a
/// <see cref="ApprovalActionResult.Failed"/> with a stable code, not an exception: the service keeps the item pending and answers 409. An exception that
/// reaches the service is a bug or an outage: the service records <see cref="ApprovalQueueRules.UnexpectedErrorCode"/> and lets it through.</para>
/// <para><b>Personal data.</b> The text is a message to a guest: an action never logs it.</para>
/// </remarks>
public interface IApprovalOriginAction
{
    /// <summary>The <see cref="ApprovalQueueItem.ItemType"/> the action serves; one action for each type.</summary>
    string ItemType { get; }

    /// <summary>The name of what it does (<see cref="ApprovalOriginActions"/>), stored in <see cref="ApprovalQueueItem.OriginAction"/>.</summary>
    string OriginAction { get; }

    /// <summary>
    /// Runs the action with <paramref name="finalText"/>: the text the host edited when it edited it, otherwise the original. Called when an item is
    /// approved, before it becomes <c>Approved</c>; when it fails the item stays pending.
    /// </summary>
    /// <param name="item">The item, as stored: its type, thread, property and the fact that it was edited (<see cref="ApprovalQueueItem.EditedContentText"/>).</param>
    /// <param name="finalText">The text to act with.</param>
    /// <param name="userId">The person who approved it (the Auth0 subject).</param>
    Task<ApprovalActionResult> ExecuteAsync(ApprovalQueueItem item, string finalText, string userId, CancellationToken ct);

    /// <summary>
    /// What a rejection does to what the item was about (a campaign goes back to a draft); nothing for a message. Also what an expiry does: the daily job calls it with the reason
    /// <see cref="ApprovalQueueRules.ExpiredReason"/> for an item nobody decided, so what the item held back is released. Idempotent: the service calls it
    /// before the item becomes <c>Rejected</c>, and a call that is repeated after a failure finds the work done.
    /// </summary>
    Task OnRejectedAsync(ApprovalQueueItem item, string reason, string userId, CancellationToken ct);
}

/// <summary>
/// The outcome of <see cref="IApprovalOriginAction.ExecuteAsync"/>. <paramref name="State"/> says how far the thing went, for the answer of the approval:
/// <c>Sent</c>, <c>Created</c>, <c>WaitingAuthorization</c> or <c>ManualVerify</c> for a message (an ambiguous send counts as done: it is checked on the
/// channel, never sent again), <c>Scheduled</c> or <c>Approved</c> for a campaign. <paramref name="ErrorCode"/> is the stable code of a failure.
/// </summary>
public sealed record ApprovalActionResult(bool Succeeded, string? State = null, string? ErrorCode = null)
{
    /// <summary>The action ran; <paramref name="state"/> says how far it went.</summary>
    public static ApprovalActionResult Executed(string? state = null) => new(true, state);

    /// <summary>The action did not run (or did not finish): <paramref name="errorCode"/> is why, a stable code of at most <see cref="ApprovalQueueItem.OriginActionErrorMaxLength"/> characters.</summary>
    public static ApprovalActionResult Failed(string errorCode)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(errorCode);
        return new ApprovalActionResult(false, null, errorCode.Length <= ApprovalQueueItem.OriginActionErrorMaxLength ? errorCode : errorCode[..ApprovalQueueItem.OriginActionErrorMaxLength]);
    }
}

/// <summary>
/// The registry of the actions, resolved by <see cref="IApprovalOriginAction.ItemType"/> (E05/F04/US01/T02): every <see cref="IApprovalOriginAction"/> of the
/// container, once for each type. A type with two actions is a mistake of the registration and stops the first use, never a silent choice of one of them.
/// </summary>
public interface IApprovalOriginActionRegistry
{
    /// <summary>The action of <paramref name="itemType"/>, or <c>null</c> when the type is a pure decision.</summary>
    IApprovalOriginAction? Find(string itemType);
}
