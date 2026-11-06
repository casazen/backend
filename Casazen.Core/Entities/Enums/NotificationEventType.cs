namespace Casazen.Core.Entities.Enums;

/// <summary>
/// The kinds of event a host user can be alerted about, one row of the preferences page for each (E05/F03, NF-65, PO-10). Stored by
/// name in <see cref="UserNotificationChannelSetting.EventType"/>, so the numbers are only a stable order for the API and a member
/// appended later never changes the meaning of the rows already written.
/// </summary>
public enum NotificationEventType
{
    /// <summary>A guest wrote in a conversation of the hub of messages (a channel or the guest area of a direct booking).</summary>
    NewGuestMessage = 1,

    /// <summary>The assistant flagged a message of a guest as urgent (E05/F05).</summary>
    UrgentGuestMessage = 2,

    /// <summary>A draft of the assistant waits for the approval of a person (E05/F04, F05).</summary>
    AiDraftToApprove = 3,

    /// <summary>A booking was made, from a channel or by a guest.</summary>
    NewBooking = 4,

    /// <summary>A booking was cancelled.</summary>
    BookingCancelled = 5,

    /// <summary>A legal deadline is near or missed (Alloggiati Web, ISTAT): it can never be fully switched off.</summary>
    LegalDeadline = 6,

    /// <summary>A synchronization with a channel failed.</summary>
    SyncError = 7,

    /// <summary>A message scheduled for a guest could not be sent.</summary>
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
