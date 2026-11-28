using System.ComponentModel.DataAnnotations;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;
using Casazen.Web.Resources;
using Microsoft.Extensions.Localization;

namespace Casazen.Web.DTOs;

public sealed class NotificationPreferencesDto
{
    public QuietHoursDto QuietHours { get; init; } = new();

    public IReadOnlyList<NotificationEventSettingDto> Events { get; init; } = [];

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

    public static IReadOnlyList<NotificationChannels> SplitChannels(NotificationChannels channels) =>
        NotificationPreferenceRules.Channels.Where(c => channels.HasFlag(c)).ToList();
}

public sealed class QuietHoursDto
{
    public TimeOnly Start { get; init; } = UserNotificationPreferences.DefaultQuietHoursStart;

    public TimeOnly End { get; init; } = UserNotificationPreferences.DefaultQuietHoursEnd;

    public bool LegalAlertsOverride { get; init; } = true;
}

public sealed class NotificationEventSettingDto
{
    public NotificationEventType EventType { get; init; }

    public IReadOnlyList<NotificationChannels> Channels { get; init; } = [];

    public bool IsDefault { get; init; }
}

public sealed class NotificationChannelAvailabilityDto
{
    public NotificationChannels Channel { get; init; }

    public bool Available { get; init; }

    public string? Reason { get; init; }
}

public sealed class SaveNotificationPreferencesRequest : IValidatableObject
{
    public const string EventInvalidMessageKey = "NotificationEventInvalid";

    public QuietHoursRequest? QuietHours { get; init; }

    public List<NotificationEventRequest>? Events { get; init; }

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (Events is not null && Events.Any(e => e is null))
        {
            var localizer = validationContext.GetService(typeof(IStringLocalizer<SharedResources>)) as IStringLocalizer;
            yield return new ValidationResult(localizer?[EventInvalidMessageKey].Value ?? EventInvalidMessageKey, [nameof(Events)]);
        }
    }
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

    [Required]
    public List<NotificationChannels>? Channels { get; init; }
}
