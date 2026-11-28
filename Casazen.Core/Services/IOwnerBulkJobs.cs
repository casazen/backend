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
    string Enqueue(Guid orgId, int year, int month, OwnerBulkAction action, IReadOnlyList<Guid> ownerIds, string? userId);

    Task<OwnerBulkRun?> GetAsync(Guid orgId, string jobId, CancellationToken ct = default);
}

public enum OwnerBulkRunStatus
{
    Queued = 1,

    Running = 2,

    Completed = 3,

    Failed = 4,
}

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
    public int Processed => Results.Count;

    public int Succeeded => Results.Count(r => r.Ok && !r.Unchanged);

    public int Unchanged => Results.Count(r => r.Ok && r.Unchanged);

    public int Failed => Results.Count(r => !r.Ok);

    public bool AllDone => Status == OwnerBulkRunStatus.Completed && Processed == Total && Failed == 0;
}
