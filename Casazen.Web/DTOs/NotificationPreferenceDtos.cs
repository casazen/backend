using System.ComponentModel.DataAnnotations;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;

namespace Casazen.Web.DTOs;

/// <summary>
/// The notification settings of the caller (<c>GET</c> and <c>PUT api/me/notification-preferences</c>, E05/F03, NF-65): the quiet hours, one entry
/// for each kind of event and the channels that can deliver today. Enums are written by name; the times are those of Europe/Rome.
/// </summary>
public sealed class NotificationPreferencesDto
{
    public QuietHoursDto QuietHours { get; init; } = new();

    /// <summary>One entry for each kind of event, in the order the page shows them.</summary>
    public IReadOnlyList<NotificationEventSettingDto> Events { get; init; } = [];

    /// <summary>Every channel and whether it can deliver; the page disables the ones that cannot, with the reason.</summary>
    public IReadOnlyList<NotificationChannelAvailabilityDto> AvailableChannels { get; init; } = [];

    public static NotificationPreferencesDto From(NotificationPreferencesView view)
    {
        ArgumentNullException.ThrowIfNull(view);
        return new NotificationPreferencesDto
        {
            QuietHours = new QuietHoursDto
            {
                Start = view.QuietHours.Start,
                End = view.QuietHours.End,
                LegalAlertsOverride = view.QuietHours.LegalAlertsOverride,
            },
            Events = view.Events
                .Select(e => new NotificationEventSettingDto
                {
                    EventType = e.EventType,
                    Channels = SplitChannels(e.Channels),
                    IsDefault = e.IsDefault,
                })
                .ToList(),
            AvailableChannels = view.AvailableChannels
                .Select(c => new NotificationChannelAvailabilityDto
                {
                    Channel = c.Channel,
                    Available = c.Available,
                    Reason = c.UnavailableReason,
                })
                .ToList(),
        };
    }

    /// <summary>The single channels of a set, in the order of <see cref="NotificationPreferenceRules.Channels"/>; empty for <c>None</c>.</summary>
    public static IReadOnlyList<NotificationChannels> SplitChannels(NotificationChannels channels) =>
        NotificationPreferenceRules.Channels.Where(c => channels.HasFlag(c)).ToList();
}

/// <summary>The quiet hours of the caller, on the clock of Europe/Rome.</summary>
public sealed class QuietHoursDto
{
    /// <summary>When they start (default 22:00).</summary>
    public TimeOnly Start { get; init; } = UserNotificationPreferences.DefaultQuietHoursStart;

    /// <summary>When they end (default 08:00); equal to <see cref="Start"/> there are none.</summary>
    public TimeOnly End { get; init; } = UserNotificationPreferences.DefaultQuietHoursEnd;

    /// <summary>A legal deadline alerts also during them (always on today).</summary>
    public bool LegalAlertsOverride { get; init; } = true;
}

public sealed class NotificationEventSettingDto
{
    public NotificationEventType EventType { get; init; }

    /// <summary>The channels of this kind of event, one name each; empty = nothing at all.</summary>
    public IReadOnlyList<NotificationChannels> Channels { get; init; } = [];

    /// <summary><c>true</c> when the caller never chose and these are the defaults of PO-10.</summary>
    public bool IsDefault { get; init; }
}

public sealed class NotificationChannelAvailabilityDto
{
    public NotificationChannels Channel { get; init; }

    public bool Available { get; init; }

    /// <summary>Why a channel cannot deliver (<c>web_push_not_available</c>); <c>null</c> when it can.</summary>
    public string? Reason { get; init; }
}

/// <summary>
/// A save of the caller's own settings. The kinds of event that are not listed stay as they are; an empty request is a 422. A kind of event in the
/// list replaces its channels with the ones given (<c>[]</c> = nothing at all, refused for the legal deadline).
/// </summary>
public sealed class SaveNotificationPreferencesRequest
{
    public QuietHoursRequest? QuietHours { get; init; }

    public List<NotificationEventRequest>? Events { get; init; }
}

public sealed class QuietHoursRequest
{
    [Required]
    public TimeOnly? Start { get; init; }

    [Required]
    public TimeOnly? End { get; init; }
}

public sealed class NotificationEventRequest
{
    [Required]
    public NotificationEventType? EventType { get; init; }

    /// <summary>The channels, one name each (<c>MobilePush</c>, <c>Email</c>, <c>CounterOnly</c>); <c>[]</c> for none. Required, so a missing list is never «none».</summary>
    [Required]
    public List<NotificationChannels>? Channels { get; init; }
}
