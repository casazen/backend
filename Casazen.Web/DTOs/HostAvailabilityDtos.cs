using System.Text.Json;
using Casazen.Core.Services;

namespace Casazen.Web.DTOs;

public sealed class HostAvailabilityDto
{
    public Guid PropertyId { get; set; }

    public DateOnly From { get; set; }

    public DateOnly To { get; set; }

    public string State { get; set; } = string.Empty;

    /// <summary>With <c>invalid</c>: <c>checkOutNotAfterCheckIn</c>, <c>checkInInPast</c> or <c>stayTooLong</c>; <c>null</c> otherwise.</summary>
    public string? InvalidReason { get; set; }

    public int Nights { get; set; }

    public int? MinNights { get; set; }

    public List<HostAvailabilityConflictDto> Conflicts { get; set; } = [];

    public HostAvailabilityWindowDto? NearestFreeWindow { get; set; }

    public Guid? LinkableBlockId { get; set; }

    /// <summary>
    /// Whether the channel manager (Octorate) was asked (E08/F01/US02/T01): <c>null</c> for a property that is not mirrored there (nothing to ask),
    /// <c>true</c> when Octorate answered (its "not available" is an <c>occupied</c> state with a <c>channelManager</c> conflict), <c>false</c>
    /// when it could not be asked (timeout, 429, 5xx, a room not chosen). <c>false</c> is advice only: it never blocks the host.
    /// </summary>
    public bool? ChannelManagerChecked { get; set; }

    public static HostAvailabilityDto Of(Guid propertyId, DateOnly from, DateOnly to, HostStayAvailability availability) => new()
    {
        PropertyId = propertyId,
        From = from,
        To = to,
        State = Name(availability.State),
        InvalidReason = availability.InvalidReason is { } reason ? Name(reason) : null,
        Nights = availability.Nights,
        MinNights = availability.MinNights,
        Conflicts = availability.Conflicts.Select(HostAvailabilityConflictDto.Of).ToList(),
        NearestFreeWindow = availability.NearestFreeWindow is { } window ? new HostAvailabilityWindowDto(window.From, window.To) : null,
        LinkableBlockId = availability.LinkableBlockId,
        ChannelManagerChecked = availability.ChannelManagerChecked,
    };

    private static string Name<TEnum>(TEnum value) where TEnum : struct, Enum => JsonNamingPolicy.CamelCase.ConvertName(value.ToString());

    internal static string ConflictType(HostAvailabilityConflictType type) => Name(type);
}

public sealed class HostAvailabilityConflictDto
{
    public Guid Id { get; set; }

    public string Type { get; set; } = string.Empty;

    public string? Title { get; set; }

    public string? Channel { get; set; }

    public DateOnly From { get; set; }

    public DateOnly To { get; set; }

    public static HostAvailabilityConflictDto Of(HostAvailabilityConflict conflict) => new()
    {
        Id = conflict.Id,
        Type = HostAvailabilityDto.ConflictType(conflict.Type),
        Title = conflict.Title,
        Channel = conflict.Channel?.ToString(),
        From = conflict.From,
        To = conflict.To,
    };
}

public sealed record HostAvailabilityWindowDto(DateOnly From, DateOnly To);
