namespace Casazen.Core.Services;

/// <summary>
/// The recurring pass over the groups of backup suppliers (SP-BKP, job <c>service-request-groups</c>, every 5 minutes): the groups whose
/// window has passed, or whose requests moved on without the group having followed, are resolved
/// (<see cref="IServiceRequestService.ResolveGroupAsync"/>): the job goes to the earliest backup that accepted when the primary did not take
/// it in its hour, the second window opens when nobody had accepted, and the requests lapse when it has passed.
/// </summary>
/// <remarks>
/// <para><b>Always on.</b> The feature flag <c>SupplierRequestBackups</c> gates only the creation of groups: a group that exists is followed
/// to the end whatever the flag says, so turning it off never leaves a request hanging. With no group a run reads one empty index and
/// stops.</para>
/// <para><b>Idempotent, one at a time.</b> A group that was resolved is no longer due; each pass runs under the group's own lock and saves
/// only what is as it was read, so a take that happens in the same moment wins and the run counts a conflict. One run at a time (a
/// PostgreSQL session lock on top of Hangfire's), at most <c>MaxPerRun</c> (500) groups per run, the oldest window first.</para>
/// </remarks>
public interface IServiceRequestGroupRunService
{
    /// <summary>One run. Never throws for a group it cannot handle: it counts it and the next run looks again.</summary>
    Task<ServiceRequestGroupRun> RunAsync(CancellationToken cancellationToken = default);
}

/// <summary>Outcome of one run of <see cref="IServiceRequestGroupRunService"/>.</summary>
/// <param name="Skipped">Another run holds the lock: nothing was read or changed.</param>
/// <param name="Examined">Groups the run looked at.</param>
/// <param name="Closed">Groups that ended: the job went to a backup, or nobody took it, or a request had moved on.</param>
/// <param name="BackupWindowsOpened">Groups that entered the second window.</param>
/// <param name="Conflicts">Groups that changed under the run: left as they are.</param>
/// <param name="Failed">Groups that could not be handled because of an error: retried by the next run.</param>
public sealed record ServiceRequestGroupRun(bool Skipped, int Examined, int Closed, int BackupWindowsOpened, int Conflicts, int Failed)
{
    /// <summary>Another run is in progress.</summary>
    public static ServiceRequestGroupRun LockTaken { get; } = new(Skipped: true, 0, 0, 0, 0, 0);

    /// <summary>Nothing was due.</summary>
    public static ServiceRequestGroupRun Empty { get; } = new(Skipped: false, 0, 0, 0, 0, 0);
}
