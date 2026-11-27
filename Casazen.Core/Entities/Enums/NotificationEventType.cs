namespace Casazen.Core.Entities.Enums;

/// <summary>
/// The kinds of event a host user can be alerted about, one row of the preferences page for each (E05/F03, NF-65, PO-10). Stored by
/// name in <see cref="UserNotificationChannelSetting.EventType"/>, so the numbers are only a stable order for the API and a member
/// appended later never changes the meaning of the rows already written.
/// </summary>
public enum NotificationEventType
{
    NewGuestMessage = 1,

    UrgentGuestMessage = 2,

    AiDraftToApprove = 3,

    NewBooking = 4,

    BookingCancelled = 5,

    /// <summary>A legal deadline is near or missed (Alloggiati Web, ISTAT): it can never be fully switched off.</summary>
    LegalDeadline = 6,

    SyncError = 7,

    ScheduledMessageIssue = 8,
}

/// <summary>
/// The ways an alert reaches a person (NF-65). A set of them is the choice of a user for one <see cref="NotificationEventType"/>; the
/// numbers are bits stored as an integer in <see cref="UserNotificationChannelSetting.Channels"/>, so they never change.
/// </summary>
/// <remarks>
/// <see cref="CounterOnly"/> is the counter of the bell (the in-app notification): alone it means «tell me in the app and nothing else».
/// <see cref="None"/> is the empty set: nothing at all, allowed for every event but <see cref="NotificationEventType.LegalDeadline"/>.
/// <see cref="BrowserPush"/> exists in the model and is reported as unavailable until a web-push infrastructure exists.
/// </remarks>
[Flags]
public enum NotificationChannels
{
    None = 0,
    MobilePush = 1,
    BrowserPush = 2,
    Email = 4,
    CounterOnly = 8,
}
