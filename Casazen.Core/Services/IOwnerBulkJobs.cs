namespace Casazen.Core.Services;

/// <summary>
/// The background run of a bulk step of the month-end for more owners than a request answers for (E13/F01/US03/T01, <see cref="OwnerBulk.SyncMaxOwners"/>): it queues the
/// run, and tells how far a run of the org is.
/// </summary>
/// <remarks>
/// A run is the Hangfire job <c>OwnerBulkCloseJob</c>; it keeps its progress with the job, so nothing is stored for it in the database. It names the org it was queued for:
/// the progress of a run of another org is never read, it is the same «not found» as a run that does not exist or that Hangfire has already forgotten.
/// </remarks>
public interface IOwnerBulkJobs
{
    /// <summary>Queues the step for the owners (already resolved by <see cref="IOwnerAccountingService.ResolveBulkOwnersAsync"/>) and answers the id of the run.</summary>
    string Enqueue(Guid orgId, int year, int month, OwnerBulkAction action, IReadOnlyList<Guid> ownerIds, string? userId);

    /// <summary>The run <paramref name="jobId"/> of the org, with the results so far; <c>null</c> when it is not a run of this org, or it is gone.</summary>
    Task<OwnerBulkRun?> GetAsync(Guid orgId, string jobId, CancellationToken ct = default);
}

/// <summary>How far a bulk run is.</summary>
public enum OwnerBulkRunStatus
{
    /// <summary>Waiting for a worker.</summary>
    Queued = 1,

    /// <summary>Working through the owners.</summary>
    Running = 2,

    /// <summary>Every owner was answered: some of the answers can still be a failure.</summary>
    Completed = 3,

    /// <summary>The run itself failed (not one of its owners): what was done before is in the results, and the step can be run again.</summary>
    Failed = 4,
}

/// <summary>
/// A bulk run: asked for <see cref="Total"/> owners, answered for <see cref="Processed"/> of them. <see cref="JobId"/> is <c>null</c> for a run that answered in the
/// request.
/// </summary>
public sealed record OwnerBulkRun(
    string? JobId,
    OwnerBulkAction Action,
    int Year,
    int Month,
    OwnerBulkRunStatus Status,
    int Total,
    IReadOnlyList<OwnerBulkOwnerResult> Results,
    string? ErrorCode = null)
{
    /// <summary>The owners answered so far.</summary>
    public int Processed => Results.Count;

    /// <summary>The owners the step was done for just now.</summary>
    public int Succeeded => Results.Count(r => r.Ok && !r.Unchanged);

    /// <summary>The owners already in the state the step leads to.</summary>
    public int Unchanged => Results.Count(r => r.Ok && r.Unchanged);

    /// <summary>The owners the step was not done for.</summary>
    public int Failed => Results.Count(r => !r.Ok);

    /// <summary>True only when the run is over, every owner was answered and none failed: never «all done» while one failed.</summary>
    public bool AllDone => Status == OwnerBulkRunStatus.Completed && Processed == Total && Failed == 0;
}
