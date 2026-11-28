namespace Casazen.Core.Services;

/// <summary>
/// Stable <c>code</c> values of the calendar changes and of their push to the channel manager (E08/F05/US02, NF-36): part of the API contract, never
/// rename one. A code is CasaZen's: it never carries a text of the provider. Each has the key of its sentence in <c>SharedResources.resx</c> (Italian)
/// and <c>SharedResources.en.resx</c>, in plain words.
/// </summary>
public static class CalendarPushErrorCodes
{
    public const string RoomNotChosen = "octorate_room_not_chosen";

    public const string RoomNotLicensed = "octorate_room_not_licensed";

    public const string QuotaLow = "octorate_quota_low";

    public const string Unavailable = "octorate_unavailable";

    public const string NotAuthorized = "octorate_not_authorized";

    public const string ChannelError = "octorate_channel_error";

    public const string Rejected = "octorate_request_rejected";

    public const string NotRetryable = "calendar_change_not_retryable";

    public const string NotFound = "calendar_change_not_found";

    public const string NoProperties = "calendar_change_no_properties";

    /// <summary>Warning of a preview: the property is not on the channel manager, the change stays in CasaZen only.</summary>
    public const string PropertyNotMirrored = "property_not_mirrored";

    /// <summary>Warning of a preview: the room of the property on the channel manager is not chosen, nothing can be sent until the host chooses it.</summary>
    public const string RoomNotChosenWarning = "room_not_chosen";

    public const string RoomNotChosenMessageKey = "CalendarPushRoomNotChosen";
    public const string RoomNotLicensedMessageKey = "CalendarPushRoomNotLicensed";
    public const string QuotaLowMessageKey = "CalendarPushQuotaLow";
    public const string UnavailableMessageKey = "CalendarPushUnavailable";
    public const string NotAuthorizedMessageKey = "CalendarPushNotAuthorized";
    public const string ChannelErrorMessageKey = "CalendarPushChannelError";
    public const string RejectedMessageKey = "CalendarPushRejected";
    public const string NotRetryableMessageKey = "CalendarChangeNotRetryable";
    public const string NotFoundMessageKey = "CalendarChangeNotFound";
    public const string NoPropertiesMessageKey = "CalendarChangeNoProperties";
    public const string PropertyNotMirroredMessageKey = "CalendarChangeWarningPropertyNotMirrored";
    public const string RoomNotChosenWarningMessageKey = "CalendarChangeWarningRoomNotChosen";

    public static IReadOnlyDictionary<string, string> MessageKeys { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        [RoomNotChosen] = RoomNotChosenMessageKey,
        [RoomNotLicensed] = RoomNotLicensedMessageKey,
        [QuotaLow] = QuotaLowMessageKey,
        [Unavailable] = UnavailableMessageKey,
        [NotAuthorized] = NotAuthorizedMessageKey,
        [ChannelError] = ChannelErrorMessageKey,
        [Rejected] = RejectedMessageKey,
        [NotRetryable] = NotRetryableMessageKey,
        [NotFound] = NotFoundMessageKey,
        [NoProperties] = NoPropertiesMessageKey,
        [PropertyNotMirrored] = PropertyNotMirroredMessageKey,
        [RoomNotChosenWarning] = RoomNotChosenWarningMessageKey,
    };

    public static string? MessageKeyOf(string? code) =>
        code is not null && MessageKeys.TryGetValue(code, out var key) ? key : null;
}
