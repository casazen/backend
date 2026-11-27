namespace Casazen.Core.Services;

/// <summary>What a person did with an item of the approval queue (<see cref="ApprovalDecision.Kind"/>). Not stored.</summary>
public enum ApprovalDecisionKind
{
    /// <summary>The item was approved and its action ran (the message went out).</summary>
    Approved = 1,

    /// <summary>The item was rejected, with its reason (<c>ApprovalQueueItem.RejectionReason</c>; <c>regenerated</c> when a new draft replaced it).</summary>
    Rejected = 2,

    /// <summary>The text of the item was edited and it still waits (<c>ApprovalQueueItem.EditedContentText</c>; <c>null</c> when the edit gave the original back).</summary>
    Edited = 3,
}

/// <summary>
/// Told about every decision on an item of the approval queue (E05/F05/US02/T01): <c>ApprovalQueueService</c> calls every registered observer after each approval, rejection and edit,
/// once the decision is saved. The lessons of the assistant (E05/F05/US02/T02) and the measure of its trust (E05/F05/US03) implement it; none is registered by this task.
/// </summary>
/// <remarks>
/// The decision is already saved when an observer runs: an observer that fails is logged and never undoes it, and the next observer is still told. An observer runs in the unit of work of
/// the request that decided (or of the job), works for <c>decision.Item.OrgId</c> only, and reads the item as it is now: the original text, the edit, the reason and who decided.
/// </remarks>
public interface IApprovalDecisionObserver
{
    Task OnDecidedAsync(ApprovalDecision decision, CancellationToken ct);
}
