using Casazen.Core.Entities.Enums;
using Casazen.Core.Utilities;

namespace Casazen.Core.Services;

/// <summary>
/// What the alerts of a host user may do, in one place and with no clock or database (E05/F03, NF-65, PO-10, ADR-025-R3): the defaults of
/// each kind of event, the rules of a save, and how the quiet hours split the channels of an alert in the ones that go now and the ones that wait.
/// <see cref="INotificationPreferenceService"/> applies them; the preferences page, the API and the tests read the same.
/// </summary>
public static class NotificationPreferenceRules
{
    /// <summary>Code of the 422 for a save that leaves <see cref="NotificationEventType.LegalDeadline"/> without any channel.</summary>
    public const string LegalChannelRequiredCode = "notification_legal_channel_required";

    /// <summary>Code of the 422 for a save that asks for a channel that does not exist yet (browser push).</summary>
    public const string ChannelUnavailableCode = "notification_channel_unavailable";

    /// <summary>The channels that can deliver today: browser push has no web-push infrastructure behind it yet.</summary>
    public const NotificationChannels Deliverable = NotificationChannels.MobilePush | NotificationChannels.Email | NotificationChannels.CounterOnly;

    /// <summary>Every bit a set of channels may carry.</summary>
    private const NotificationChannels AllChannels = Deliverable | NotificationChannels.BrowserPush;

    /// <summary>The channels the quiet hours hold back: the ones that interrupt a person. The counter of the bell never waits.</summary>
    private const NotificationChannels HeldByQuietHours = NotificationChannels.MobilePush | NotificationChannels.Email;

    /// <summary>The single channels, in the order the page shows them.</summary>
    public static IReadOnlyList<NotificationChannels> Channels { get; } =
        [NotificationChannels.MobilePush, NotificationChannels.BrowserPush, NotificationChannels.Email, NotificationChannels.CounterOnly];

    /// <summary>The kinds of event, in the order the page shows them.</summary>
    public static IReadOnlyList<NotificationEventType> EventTypes { get; } = Enum.GetValues<NotificationEventType>();

    /// <summary>
    /// The default of PO-10 for a kind of event: a new or urgent message, a draft to approve and a new booking are a mobile push and the counter;
    /// a legal deadline adds the email; a cancellation, a sync error and a scheduled message issue are the counter only.
    /// </summary>
    public static NotificationChannels DefaultChannelsOf(NotificationEventType type) => type switch
    {
        NotificationEventType.NewGuestMessage
            or NotificationEventType.UrgentGuestMessage
            or NotificationEventType.AiDraftToApprove
            or NotificationEventType.NewBooking => NotificationChannels.MobilePush | NotificationChannels.CounterOnly,
        NotificationEventType.LegalDeadline =>
            NotificationChannels.MobilePush | NotificationChannels.CounterOnly | NotificationChannels.Email,
        NotificationEventType.BookingCancelled
            or NotificationEventType.SyncError
            or NotificationEventType.ScheduledMessageIssue => NotificationChannels.CounterOnly,
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, "Unknown kind of notification event."),
    };

    /// <summary>A legal deadline (Alloggiati, ISTAT): it can never be fully switched off and it overrides the quiet hours.</summary>
    public static bool IsLegal(NotificationEventType type) => type == NotificationEventType.LegalDeadline;

    /// <summary>True when <paramref name="channel"/> (one bit) can deliver today.</summary>
    public static bool IsAvailable(NotificationChannels channel) => (channel & Deliverable) == channel && channel != NotificationChannels.None;

    /// <summary>
    /// The channels of a kind of event for a user: what the user saved, or the default when it saved nothing (<paramref name="stored"/>
    /// <c>null</c>). The channels of a legal deadline are never empty: a stored set that cannot deliver anything (data written by hand, a channel
    /// that stopped being available) is replaced by the default.
    /// </summary>
    public static NotificationChannels EffectiveChannelsOf(NotificationEventType type, NotificationChannels? stored)
    {
        var channels = stored ?? DefaultChannelsOf(type);
        return IsLegal(type) && (channels & Deliverable) == NotificationChannels.None ? DefaultChannelsOf(type) : channels;
    }

    /// <summary>
    /// The code of the 422 that a save of <paramref name="channels"/> for <paramref name="type"/> deserves, or <c>null</c> when it is fine: a
    /// channel that does not exist yet (<see cref="ChannelUnavailableCode"/>), or no channel at all for a legal deadline
    /// (<see cref="LegalChannelRequiredCode"/>).
    /// </summary>
    public static string? ValidateSave(NotificationEventType type, NotificationChannels channels)
    {
        if ((channels & ~Deliverable) != NotificationChannels.None)
            return ChannelUnavailableCode;

        return IsLegal(type) && channels == NotificationChannels.None ? LegalChannelRequiredCode : null;
    }

    /// <summary>True when the bits of <paramref name="channels"/> are all declared channels (a value read from a client or a column).</summary>
    public static bool IsDeclared(NotificationChannels channels) => (channels & ~AllChannels) == NotificationChannels.None;

    /// <summary>
    /// How the quiet hours split the channels of an alert at <paramref name="nowUtc"/> (NF-65, ADR-025-R3): the counter always goes now; the
    /// mobile push and the email wait for the end of the quiet hours of the user, on the clock of Europe/Rome, unless the alert is a legal
    /// deadline and <paramref name="legalOverridesQuietHours"/> is on. A channel that cannot deliver (browser push) is dropped.
    /// </summary>
    /// <param name="type">The kind of event.</param>
    /// <param name="channels">What the user chose (<see cref="EffectiveChannelsOf"/>).</param>
    /// <param name="quietStart">When the quiet hours start (Europe/Rome).</param>
    /// <param name="quietEnd">When they end (Europe/Rome); equal to the start there are no quiet hours.</param>
    /// <param name="legalOverridesQuietHours"><c>UserNotificationPreferences.LegalAlertsOverrideQuietHours</c>.</param>
    /// <param name="nowUtc">The instant of the event.</param>
    public static ChannelDecision Decide(
        NotificationEventType type,
        NotificationChannels channels,
        TimeOnly quietStart,
        TimeOnly quietEnd,
        bool legalOverridesQuietHours,
        DateTime nowUtc)
    {
        var deliverable = channels & Deliverable;
        var held = deliverable & HeldByQuietHours;
        if (held == NotificationChannels.None || (IsLegal(type) && legalOverridesQuietHours))
            return new ChannelDecision(deliverable, NotificationChannels.None, null);

        return QuietHoursEndUtc(quietStart, quietEnd, nowUtc) is { } due
            ? new ChannelDecision(deliverable & ~HeldByQuietHours, held, due)
            : new ChannelDecision(deliverable, NotificationChannels.None, null);
    }

    /// <summary>
    /// The instant the quiet hours that contain <paramref name="nowUtc"/> end, or <c>null</c> when <paramref name="nowUtc"/> is outside them.
    /// The window is <c>[start, end)</c> on the clock of Europe/Rome (22:00 stays 22:00 all year, also on the two days the clock changes); a
    /// window that ends before it starts runs over midnight, and start equal to end is an empty window.
    /// </summary>
    public static DateTime? QuietHoursEndUtc(TimeOnly start, TimeOnly end, DateTime nowUtc)
    {
        var local = RomeCalendar.ToRome(nowUtc).DateTime;
        var time = TimeOnly.FromDateTime(local);
        if (!time.IsBetween(start, end))
            return null;

        // In the evening part of a window over midnight the quiet hours end the next morning, in every other case today.
        var endsTomorrow = start > end && time >= start;
        var endDate = DateOnly.FromDateTime(local).AddDays(endsTomorrow ? 1 : 0);
        var until = RomeCalendar.ToUtc(endDate, end);

        // The hour that happens twice when the clocks go back can put the end of the window at an instant that has already passed.
        return until > nowUtc ? until : null;
    }
}

/// <summary>
/// The channels of an alert split by the quiet hours: <see cref="Now"/> goes at once, <see cref="Deferred"/> waits for
/// <see cref="DueAtUtc"/> (<c>null</c> when nothing waits).
/// </summary>
public readonly record struct ChannelDecision(NotificationChannels Now, NotificationChannels Deferred, DateTime? DueAtUtc);
