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

    public const string D6ChannelFailed = "d6_channel_failed";

    public const string ChannelConnectionNotFound = "channel_connection_not_found";

    public const string ChannelAccessNotValid = "channel_access_not_valid";

    public const string MappingRefreshConflict = "mapping_refresh_conflict";

    public const string ReservationImportIncomplete = "reservation_import_incomplete";

    public const string RoomNotChosen = "octorate_room_not_chosen";

    public const string MappingNotConfirmed = "octorate_mapping_not_confirmed";
}
