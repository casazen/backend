namespace Casazen.Core.Entities.Enums;

/// <summary>
/// Where one chunk of a change of the calendar stands (E08/F05/US01/T01, NF-36): a chunk is a range of dates of one property sent to the
/// channel manager in one call. Stored as an integer; append-only, a number is never reused.
/// </summary>
public enum CalendarChunkState
{
    /// <summary>Still to send (or to send again). Stored as 1.</summary>
    Pending = 1,

    /// <summary>The channel manager accepted the call and gave the id of the process it runs. Stored as 2.</summary>
    Accepted = 2,

    /// <summary>The process of the channel manager finished: the dates are updated there. Stored as 3.</summary>
    Confirmed = 3,

    /// <summary>The chunk could not be sent, with a stable error code. Stored as 4.</summary>
    Failed = 4,

    /// <summary>Nothing to send for this chunk (for example the property is no longer mirrored). Stored as 5.</summary>
    Skipped = 5,
}
