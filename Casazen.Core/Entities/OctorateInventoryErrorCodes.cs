namespace Casazen.Core.Entities;

/// <summary>
/// The stable error codes of an import of existing OTA inventory (E02/F01/US03), stored in <c>ErrorCode</c> of a channel or of one of its items
/// and spoken by the API. Never rename one: the web wizard translates them. An error of Octorate keeps the code of its own exception
/// (<c>octorate_not_connected</c>, <c>octorate_auth_failed</c>, <c>octorate_wrong_environment</c>, <c>octorate_rate_limited</c>,
/// <c>octorate_request_rejected</c>, <c>octorate_not_found</c>, <c>octorate_conflict</c>).
/// </summary>
public static class OctorateInventoryErrorCodes
{
    /// <summary>
    /// The operation is not verified on a real property for this channel (its capability flag is off): it is not called and the step stays
    /// guided. Never a success that Octorate did not confirm.
    /// </summary>
    public const string D6OperationUnverified = "d6_operation_unverified";

    /// <summary>
    /// The result of the operation is unknown (no answer in time, a failure of Octorate, an answer that cannot be read, or a request closed after
    /// the call left): the item is marked with it BEFORE the call, so it is never asked again by itself. A person checks the panel and asks
    /// again by naming the listing in <c>retryUnknown</c>.
    /// </summary>
    public const string D6ResultUnknown = "d6_result_unknown";

    /// <summary>Octorate answered that the channel itself failed (its «partner failure», HTTP 550).</summary>
    public const string D6ChannelFailed = "d6_channel_failed";

    /// <summary>The access is given but Octorate shows no connection of this channel: nothing can be imported, and nothing is created to fix it.</summary>
    public const string ChannelConnectionNotFound = "channel_connection_not_found";

    /// <summary>The check of the access found it refused or not valid.</summary>
    public const string ChannelAccessNotValid = "channel_access_not_valid";

    /// <summary>Another read of the listings of the org was running: the mapping is read again at the next command.</summary>
    public const string MappingRefreshConflict = "mapping_refresh_conflict";

    /// <summary>The reservations were not all read into CasaZen (the run stopped before the end): the next command reads again.</summary>
    public const string ReservationImportIncomplete = "reservation_import_incomplete";

    /// <summary>The listing is confirmed but its room is not chosen: the reservations cannot be assigned.</summary>
    public const string RoomNotChosen = "octorate_room_not_chosen";

    /// <summary>The listing is no longer confirmed to a property of CasaZen.</summary>
    public const string MappingNotConfirmed = "octorate_mapping_not_confirmed";
}
