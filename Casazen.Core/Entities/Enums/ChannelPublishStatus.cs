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
    /// <summary>Nothing was sent to the channel yet.</summary>
    Draft = 0,

    /// <summary>A push to the channel is running.</summary>
    Sending = 1,

    /// <summary>The channel manager took the content; the channel itself has not confirmed it yet.</summary>
    Accepted = 2,

    /// <summary>The content is live on the channel.</summary>
    Published = 3,

    /// <summary>The channel refused the content, or something is missing: the person has to fix it.</summary>
    ToFix = 4,

    /// <summary>The content is written on the channel itself (Expedia): CasaZen sends nothing to it.</summary>
    ManagedOnChannel = 5,
}
