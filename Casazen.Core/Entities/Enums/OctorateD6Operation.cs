namespace Casazen.Core.Entities.Enums;

/// <summary>
/// One of the four operations of the D6 sequence that CasaZen asks of Octorate for an existing listing (<c>IMPORT_ROOMS</c>,
/// <c>IMPORT_RESERVATIONS</c>, <c>IMPORT_CALENDAR</c>, <c>MARK_CONNECTED</c>). It is also the unit of the per-channel capability flags: an
/// operation not verified on a real property for a channel is not called (E02/F01/US03, <c>Octorate:ExistingInventory:Channels</c>). No
/// member creates, publishes or pushes anything: the import never needs one.
/// </summary>
public enum OctorateD6Operation
{
    /// <summary><c>IMPORT_ROOMS</c>: the rooms of the channel, ready to be mapped.</summary>
    ImportRooms = 1,

    /// <summary><c>IMPORT_RESERVATIONS</c>: the future reservations of the channel, inside Octorate.</summary>
    ImportReservations = 2,

    /// <summary><c>IMPORT_CALENDAR</c>: the calendar of the channel, inside Octorate.</summary>
    ImportCalendar = 3,

    /// <summary><c>MARK_CONNECTED</c>: tells Octorate, and the channel when needed, that the connection is ready.</summary>
    MarkConnected = 4,
}
