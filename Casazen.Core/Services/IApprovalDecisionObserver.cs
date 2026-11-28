namespace Casazen.Core.Services;

public enum ApprovalDecisionKind
{
    Approved = 1,

    Rejected = 2,

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
