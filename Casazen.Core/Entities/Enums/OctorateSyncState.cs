namespace Casazen.Core.Entities.Enums;

/// <summary>
/// Where the mirror of a CasaZen booking on Octorate stands (<see cref="OctorateBookingSync.State"/>, E08/F01/US01/T01, ADR-031). Stored as
/// its number and append-only: a number never changes and is never reused. A booking whose property is not mirrored on Octorate has no
/// sync row at all: the absence of the row means "not mirrored", never one of these states.
/// </summary>
public enum OctorateSyncState
{
    Pending = 1,

    Synced = 2,

    Failed = 3,

    /// <summary>Octorate refused the operation because of its own data (the dates are taken, the reservation changed there).</summary>
    Conflict = 4,

    NotAllowedByChannel = 5,
}
