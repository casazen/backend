namespace Casazen.Core.Entities.Enums;

/// <summary>
/// Where the mirror of a CasaZen booking on Octorate stands (<see cref="OctorateBookingSync.State"/>, E08/F01/US01/T01, ADR-031). Stored as
/// its number and append-only: a number never changes and is never reused. A booking whose property is not mirrored on Octorate has no
/// sync row at all: the absence of the row means "not mirrored", never one of these states.
/// </summary>
public enum OctorateSyncState
{
    /// <summary>An operation was started and its outcome is not known yet (in flight, or the process stopped during it).</summary>
    Pending = 1,

    /// <summary>Octorate confirmed the last operation: the reservation holds what CasaZen holds.</summary>
    Synced = 2,

    /// <summary>The last operation failed (<see cref="OctorateBookingSync.ErrorCode"/> says why); the host can ask for it again.</summary>
    Failed = 3,

    /// <summary>Octorate refused the operation because of its own data (the dates are taken, the reservation changed there).</summary>
    Conflict = 4,

    /// <summary>The channel of the reservation does not allow the operation: nothing is sent and nothing pretends it was.</summary>
    NotAllowedByChannel = 5,
}
