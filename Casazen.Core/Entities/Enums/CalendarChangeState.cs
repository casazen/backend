namespace Casazen.Core.Entities.Enums;

/// <summary>
/// Where a change of the calendar stands (E08/F05/US01/T01, NF-36): what the host reads as «saved», «sending», «sent» or «confirmed». The state
/// is that of the process, derived from its chunks; <see cref="Sent"/> is the channel manager accepting the work, which is not the publication
/// on the portals. Stored as an integer; append-only, a number is never reused.
/// </summary>
public enum CalendarChangeState
{
    Applied = 1,

    Queued = 2,

    Sending = 3,

    Sent = 4,

    Confirmed = 5,

    PartiallySent = 6,

    Failed = 7,
}
