namespace Casazen.Core.Services;

/// <summary>
/// The stable codes of the mirror of a booking on Octorate (<c>OctorateBookingSync.ErrorCode</c> and the answers of the retry endpoint,
/// E08/F01/US01/T03): CasaZen's own words, never a text of Octorate. Each has its message in both resource files
/// (<see cref="MessageKeys"/>) for the screens that show the state of a booking.
/// </summary>
public static class OctorateSyncErrorCodes
{
    /// <summary>Octorate refused the dates (a 409 or an availability refusal): the nights are taken on the channel manager.</summary>
    public const string DatesUnavailable = "octorate_dates_unavailable";

    /// <summary>Octorate did not answer, answered 429 or 5xx, or the direct channel is not configured: tried again later.</summary>
    public const string Unavailable = "octorate_unavailable";

    /// <summary>Octorate refused the request itself (another 4xx): not tried again by itself.</summary>
    public const string Rejected = "octorate_rejected";

    /// <summary>The reservation changed on Octorate since CasaZen read it (a 409 of a change, E08/F02).</summary>
    public const string ReservationChanged = "octorate_reservation_changed";

    /// <summary>The mapping of the property is confirmed but its room is not chosen: the host chooses it in the mapping step.</summary>
    public const string RoomNotChosen = "octorate_room_not_chosen";

    /// <summary>The retry endpoint was called for a sync that is neither failed nor in conflict (409).</summary>
    public const string NotRetryable = "octorate_sync_not_retryable";

    /// <summary>
    /// The reservation is on Octorate but a payment of the booking could not be recorded on it (Octorate did not answer or refused it, or
    /// the way it was collected has no <c>paymentMode</c> configured): the delivery is repeated, and the host can ask again.
    /// </summary>
    public const string PaymentsNotRecorded = "octorate_payments_not_recorded";

    /// <summary>The resource key of the message of each code (Italian and English).</summary>
    public static IReadOnlyDictionary<string, string> MessageKeys { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        [DatesUnavailable] = "OctorateSyncDatesUnavailable",
        [Unavailable] = "OctorateSyncUnavailable",
        [Rejected] = "OctorateSyncRejected",
        [ReservationChanged] = "OctorateSyncReservationChanged",
        [RoomNotChosen] = "OctorateSyncRoomNotChosen",
        [NotRetryable] = "OctorateSyncNotRetryable",
        [PaymentsNotRecorded] = "OctorateSyncPaymentsNotRecorded",
    };
}
