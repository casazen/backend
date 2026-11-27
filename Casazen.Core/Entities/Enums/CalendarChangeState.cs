namespace Casazen.Core.Entities.Enums;

/// <summary>
/// Where a change of the calendar stands (E08/F05/US01/T01, NF-36): what the host reads as «saved», «sending», «sent» or «confirmed». The state
/// is that of the process, derived from its chunks; <see cref="Sent"/> is the channel manager accepting the work, which is not the publication
/// on the portals. Stored as an integer; append-only, a number is never reused.
/// </summary>
public enum CalendarChangeState
{
    /// <summary>Saved in CasaZen and nothing to send (the property is not mirrored on the channel manager). Stored as 1.</summary>
    Applied = 1,

    /// <summary>Saved in CasaZen and waiting to be sent. Stored as 2.</summary>
    Queued = 2,

    /// <summary>Being sent: some chunks are accepted, others are still to send. Stored as 3.</summary>
    Sending = 3,

    /// <summary>Every chunk was accepted by the channel manager. Stored as 4.</summary>
    Sent = 4,

    /// <summary>Every chunk was confirmed by the process the channel manager ran for it. Stored as 5.</summary>
    Confirmed = 5,

    /// <summary>Some chunks were accepted and others failed. Stored as 6.</summary>
    PartiallySent = 6,

    /// <summary>No chunk could be sent. Stored as 7.</summary>
    Failed = 7,
}
