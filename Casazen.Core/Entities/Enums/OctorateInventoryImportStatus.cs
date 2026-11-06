namespace Casazen.Core.Entities.Enums;

/// <summary>
/// Where an import of existing OTA inventory stands (D6, E02/F01/US03), for a batch and for each of its channels. Stored as its number in
/// <c>OctorateInventoryImportBatches.Status</c> and <c>OctorateInventoryImportChannels.Status</c>: every member has an explicit value and an
/// existing number never changes. The first seven are the steps in the order they run (the batch shows the earliest one a channel still has
/// to go through); the last three end or stop a channel.
/// </summary>
public enum OctorateInventoryImportStatus
{
    /// <summary>The channel is chosen and nothing was started.</summary>
    Draft = 1,

    /// <summary>The person was sent to the Octorate panel to give CasaZen's channel manager the access to the channel, and the result is awaited.</summary>
    AwaitingChannelAccess = 2,

    /// <summary>The access is given: the rooms of the channel are being imported into the channel manager (<c>IMPORT_ROOMS</c>).</summary>
    ImportingRooms = 3,

    /// <summary>The rooms are imported and the listings are read: a person confirms, chooses or leaves for later each one. Nothing else runs.</summary>
    AwaitingMapping = 4,

    /// <summary>The reservations of the confirmed listings are being imported (<c>IMPORT_RESERVATIONS</c> and the import into CasaZen).</summary>
    ImportingReservations = 5,

    /// <summary>The calendars of the confirmed listings are being imported (<c>IMPORT_CALENDAR</c>).</summary>
    ImportingCalendar = 6,

    /// <summary>The confirmed listings are being marked as connected (<c>MARK_CONNECTED</c>).</summary>
    MarkingConnected = 7,

    /// <summary>Every listing of the channel is connected, left for later or rejected: nothing is left for the import to do.</summary>
    Completed = 8,

    /// <summary>Octorate refused the operation or the channel failed on its side: a stable error code says which. A later command tries again.</summary>
    Failed = 9,

    /// <summary>
    /// A person (or time) has to act before the import goes on: an operation not verified yet for the channel, an unknown result, the rate
    /// limit, an access to confirm, a connection to remake. A stable error code says which; a later command resumes from the last durable step.
    /// </summary>
    NeedsAttention = 10,
}
