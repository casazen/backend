namespace Casazen.Core.Entities.Enums;

/// <summary>
/// Where a group of backup suppliers is (<see cref="ServiceRequestGroup.Phase"/>). Stored as an integer: never reorder or
/// renumber, only append.
/// </summary>
public enum ServiceRequestGroupPhase
{
    /// <summary>
    /// The primary has its window to take the job (<see cref="ServiceRequestGroup.PrimaryDueAt"/>). The backups can only say they
    /// would take it if the primary does not (the pre-approval), or refuse.
    /// </summary>
    PrimaryWindow = 0,

    /// <summary>
    /// The window of the primary passed with nobody having taken the job (or the primary refused it): every supplier of the group
    /// that is still waiting can take it until <see cref="ServiceRequestGroup.BackupDueAt"/>, and the first to do so has it.
    /// </summary>
    BackupWindow = 1,

    /// <summary>The group is over: someone took the job, or nobody did and the requests lapsed, or the host cancelled.</summary>
    Closed = 2,
}
