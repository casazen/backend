namespace Casazen.Core.Entities.Enums;

/// <summary>
/// Where an import of existing OTA inventory stands (D6, E02/F01/US03), for a batch and for each of its channels. Stored as its number in
/// <c>OctorateInventoryImportBatches.Status</c> and <c>OctorateInventoryImportChannels.Status</c>: every member has an explicit value and an
/// existing number never changes. The first seven are the steps in the order they run (the batch shows the earliest one a channel still has
/// to go through); the last three end or stop a channel.
/// </summary>
public enum OctorateInventoryImportStatus
{
    Draft = 1,

    AwaitingChannelAccess = 2,

    ImportingRooms = 3,

    AwaitingMapping = 4,

    ImportingReservations = 5,

    ImportingCalendar = 6,

    MarkingConnected = 7,

    Completed = 8,

    Failed = 9,

    NeedsAttention = 10,
}
