namespace Casazen.Core.Services;

/// <summary>
/// The stable codes of the mirror of a booking on Octorate (<c>OctorateBookingSync.ErrorCode</c> and the answers of the retry endpoint,
/// E08/F01/US01/T03): CasaZen's own words, never a text of Octorate. Each has its message in both resource files
/// (<see cref="MessageKeys"/>) for the screens that show the state of a booking.
/// </summary>
public static class OctorateSyncErrorCodes
{
    public const string DatesUnavailable = "octorate_dates_unavailable";

    public const string Unavailable = "octorate_unavailable";

    public const string Rejected = "octorate_rejected";

    /// <summary>The reservation changed on Octorate since CasaZen read it (a 409 of a change, E08/F02).</summary>
    public const string ReservationChanged = "octorate_reservation_changed";

    public const string RoomNotChosen = "octorate_room_not_chosen";

    /// <summary>The retry endpoint was called for a sync that is neither failed nor in conflict (409).</summary>
    public const string NotRetryable = "octorate_sync_not_retryable";

    public const string PaymentsNotRecorded = "octorate_payments_not_recorded";

    public const string ReservationLinkedElsewhere = "octorate_reservation_linked_elsewhere";

    public const string NotMirrored = "octorate_not_mirrored";

    public static IReadOnlyDictionary<string, string> MessageKeys { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        [DatesUnavailable] = "OctorateSyncDatesUnavailable",
        [Unavailable] = "OctorateSyncUnavailable",
        [Rejected] = "OctorateSyncRejected",
        [ReservationChanged] = "OctorateSyncReservationChanged",
        [RoomNotChosen] = "OctorateSyncRoomNotChosen",
        [NotRetryable] = "OctorateSyncNotRetryable",
        [PaymentsNotRecorded] = "OctorateSyncPaymentsNotRecorded",
        [ReservationLinkedElsewhere] = "OctorateSyncLinkedElsewhere",
        [NotMirrored] = "OctorateSyncNotMirrored",
    };
}
