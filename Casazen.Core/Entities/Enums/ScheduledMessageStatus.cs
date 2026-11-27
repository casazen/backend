namespace Casazen.Core.Entities.Enums;

/// <summary>
/// Where a scheduled message of a booking stands (E05/F07/US01, NF-71, MSG-11): stored as the number, so a member is never renumbered and a later one takes the next
/// number. A row ends in <see cref="Sent"/>, <see cref="Skipped"/>, <see cref="Cancelled"/> or <see cref="Expired"/>; <see cref="Scheduled"/>, <see cref="Blocked"/> and
/// <see cref="Failed"/> are the open ones, the three that the send tries again.
/// </summary>
public enum ScheduledMessageStatus
{
    /// <summary>Planned: it goes out at its time (the routine that fires, or the sweep that catches it up).</summary>
    Scheduled = 1,

    /// <summary>It went out (or the message layer holds it and tells the channel; a send whose outcome nobody knows counts as sent and is never sent again).</summary>
    Sent = 2,

    /// <summary>Not sent, on purpose: a person skipped it, or the system could not reach the guest by the rules of the channel (the reason says which).</summary>
    Skipped = 3,

    /// <summary>Held back: a piece of data is missing (a variable, the access code of a guest not verified); the host has a «To do» and the sweep tries again.</summary>
    Blocked = 4,

    /// <summary>The last send did not go: the channel refused it or could not be reached; the sweep tries again a few times, and the host can send it now.</summary>
    Failed = 5,

    /// <summary>The booking was cancelled or the routine no longer holds the message: it will never go out.</summary>
    Cancelled = 6,

    /// <summary>Its moment passed for good: it stayed blocked until the check-out, or it was moved into the past.</summary>
    Expired = 7,
}
