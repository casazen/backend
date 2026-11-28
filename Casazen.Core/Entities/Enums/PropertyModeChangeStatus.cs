namespace Casazen.Core.Entities.Enums;

/// <summary>
/// Where a scheduled change of rental mode is (<see cref="PropertyModeChange.Status"/>, PM-02). Stored as an integer in
/// <c>PropertyModeChanges.Status</c>: <b>append only</b>, never renumber or reuse a value. The partial unique index that
/// allows one open change per property is written with <see cref="Scheduled"/> = 0.
/// </summary>
public enum PropertyModeChangeStatus
{
    Scheduled = 0,

    /// <summary>Done: the property is in <see cref="PropertyModeChange.ToMode"/> since <see cref="PropertyModeChange.AppliedAt"/>.</summary>
    Applied = 1,

    Cancelled = 2,

    Failed = 3,
}
