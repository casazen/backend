namespace Casazen.Core.Entities.Enums;

/// <summary>
/// Where a scheduled message of a booking stands (E05/F07/US01, NF-71, MSG-11): stored as the number, so a member is never renumbered and a later one takes the next
/// number. A row ends in <see cref="Sent"/>, <see cref="Skipped"/>, <see cref="Cancelled"/> or <see cref="Expired"/>; <see cref="Scheduled"/>, <see cref="Blocked"/> and
/// <see cref="Failed"/> are the open ones, the three that the send tries again.
/// </summary>
public enum ScheduledMessageStatus
{
    Scheduled = 1,

    Sent = 2,

    Skipped = 3,

    Blocked = 4,

    Failed = 5,

    Cancelled = 6,

    Expired = 7,
}
