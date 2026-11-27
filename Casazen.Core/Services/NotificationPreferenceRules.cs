using Casazen.Core.Entities.Enums;
using Casazen.Core.Utilities;

namespace Casazen.Core.Services;

public static class NotificationPreferenceRules
{
    public const string LegalChannelRequiredCode = "notification_legal_channel_required";

    public const string ChannelUnavailableCode = "notification_channel_unavailable";

    public const NotificationChannels Deliverable = NotificationChannels.MobilePush | NotificationChannels.Email | NotificationChannels.CounterOnly;

    private const NotificationChannels AllChannels = Deliverable | NotificationChannels.BrowserPush;

    private const NotificationChannels HeldByQuietHours = NotificationChannels.MobilePush | NotificationChannels.Email;

    public static IReadOnlyList<NotificationChannels> Channels { get; } =
        [NotificationChannels.MobilePush, NotificationChannels.BrowserPush, NotificationChannels.Email, NotificationChannels.CounterOnly];

    public static IReadOnlyList<NotificationEventType> EventTypes { get; } = Enum.GetValues<NotificationEventType>();

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

    public static string? ValidateSave(NotificationEventType type, NotificationChannels channels)
    {
        if ((channels & ~Deliverable) != NotificationChannels.None)
            return ChannelUnavailableCode;

        return IsLegal(type) && channels == NotificationChannels.None ? LegalChannelRequiredCode : null;
    }

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

    public static DateTime? QuietHoursEndUtc(TimeOnly start, TimeOnly end, DateTime nowUtc)
    {
        var local = RomeCalendar.ToRome(nowUtc).DateTime;
        var time = TimeOnly.FromDateTime(local);
        if (!time.IsBetween(start, end))
            return null;

        var endsTomorrow = start > end && time >= start;
        var endDate = DateOnly.FromDateTime(local).AddDays(endsTomorrow ? 1 : 0);
        var until = RomeCalendar.ToUtc(endDate, end);

        return until > nowUtc ? until : null;
    }
}

public readonly record struct ChannelDecision(NotificationChannels Now, NotificationChannels Deferred, DateTime? DueAtUtc);
