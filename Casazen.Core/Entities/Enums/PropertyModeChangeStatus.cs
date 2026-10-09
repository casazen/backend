namespace Casazen.Core.Entities.Enums;

/// <summary>
/// Where a scheduled change of rental mode is (<see cref="PropertyModeChange.Status"/>, PM-02). Stored as an integer in
/// <c>PropertyModeChanges.Status</c>: <b>append only</b>, never renumber or reuse a value. The partial unique index that
/// allows one open change per property is written with <see cref="Scheduled"/> = 0.
/// </summary>
public enum PropertyModeChangeStatus
{
    /// <summary>Waiting for its day: the property is still in <see cref="PropertyModeChange.FromMode"/>. At most one per property.</summary>
    Scheduled = 0,

    /// <summary>Done: the property is in <see cref="PropertyModeChange.ToMode"/> since <see cref="PropertyModeChange.AppliedAt"/>.</summary>
    Applied = 1,

    /// <summary>Withdrawn by a person before its day. Nothing changed.</summary>
    Cancelled = 2,

    /// <summary>
    /// The daily job could not apply it: on its day the stays, blocks or leases no longer allowed the date
    /// (<see cref="PropertyModeChange.FailureReason"/>), or the property was gone. Nothing changed; the host was told.
    /// </summary>
    Failed = 3,
}
