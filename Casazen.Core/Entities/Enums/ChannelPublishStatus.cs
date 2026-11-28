namespace Casazen.Core.Entities.Enums;

/// <summary>
/// Where the content of a property stands on one channel of the channel manager (NF-27, NF-38, ADR-034): the six states of the life of a
/// publication. Stored as an integer in <c>ListingChannelStates.Status</c>, <b>append only</b>: never renumber a member.
/// </summary>
/// <remarks>
/// A channel manager that takes the content (<see cref="Accepted"/>) has not published it on the channel yet: the move to
/// <see cref="Published"/> or <see cref="ToFix"/> is made only when the status of the process says so, never on the answer of the push.
/// </remarks>
public enum ChannelPublishStatus
{
    Draft = 0,

    Sending = 1,

    Accepted = 2,

    Published = 3,

    ToFix = 4,

    ManagedOnChannel = 5,
}
