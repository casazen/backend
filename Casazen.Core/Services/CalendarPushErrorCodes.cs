namespace Casazen.Core.Services;

/// <summary>
/// Stable <c>code</c> values of the calendar changes and of their push to the channel manager (E08/F05/US02, NF-36): part of the API contract, never
/// rename one. A code is CasaZen's: it never carries a text of the provider. Each has the key of its sentence in <c>SharedResources.resx</c> (Italian)
/// and <c>SharedResources.en.resx</c>, in plain words.
/// </summary>
public static class CalendarPushErrorCodes
{
    /// <summary>The mapping of the property is confirmed but its room is not chosen: the host chooses it in the mapping step.</summary>
    public const string RoomNotChosen = "octorate_room_not_chosen";

    /// <summary>The channel manager answered 200 and wrote nothing (no process id): the room has no active license there. Never taken for accepted.</summary>
    public const string RoomNotLicensed = "octorate_room_not_licensed";

    /// <summary>The quota of calls of the channel manager is nearly used: the chunk waits and is sent later. Never a failure.</summary>
    public const string QuotaLow = "octorate_quota_low";

    /// <summary>The channel manager did not answer (rate limit, 5xx, timeout) and the attempts are used up.</summary>
    public const string Unavailable = "octorate_unavailable";

    /// <summary>The channel manager refused the access of CasaZen (a refusal of the credentials or the token, or the wrong environment).</summary>
    public const string NotAuthorized = "octorate_not_authorized";

    /// <summary>The channel manager took the change and reported an error while sending it to the channels.</summary>
    public const string ChannelError = "octorate_channel_error";

    /// <summary>The channel manager refused the request itself (another 4xx, or an answer that says it did not take it).</summary>
    public const string Rejected = "octorate_request_rejected";

    /// <summary>409: only a failed or partly sent change can be sent again.</summary>
    public const string NotRetryable = "calendar_change_not_retryable";

    /// <summary>404: no change with that id for the caller (another org's, or one of a property it does not reach).</summary>
    public const string NotFound = "calendar_change_not_found";

    /// <summary>422: a change names no property.</summary>
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

    /// <summary>The resource key of the sentence of each code (Italian and English).</summary>
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

    /// <summary>The key of the sentence of <paramref name="code"/>; <c>null</c> for a code this class does not know.</summary>
    public static string? MessageKeyOf(string? code) =>
        code is not null && MessageKeys.TryGetValue(code, out var key) ? key : null;
}
