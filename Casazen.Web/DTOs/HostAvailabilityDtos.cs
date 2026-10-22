using System.Text.Json;
using Casazen.Core.Services;

namespace Casazen.Web.DTOs;

/// <summary>
/// The answer of <c>GET /api/properties/{id}/availability</c> (SR-06a). <c>state</c> is <c>ok</c>, <c>belowMinimum</c>, <c>occupied</c>,
/// <c>linkableBlock</c> or <c>invalid</c>; the words of the other fields are on their properties.
/// </summary>
public sealed class HostAvailabilityDto
{
    public Guid PropertyId { get; set; }

    /// <summary>Arrival day asked.</summary>
    public DateOnly From { get; set; }

    /// <summary>Departure day asked.</summary>
    public DateOnly To { get; set; }

    /// <summary><c>ok</c>, <c>belowMinimum</c>, <c>occupied</c>, <c>linkableBlock</c> or <c>invalid</c>.</summary>
    public string State { get; set; } = string.Empty;

    /// <summary>With <c>invalid</c>: <c>checkOutNotAfterCheckIn</c>, <c>checkInInPast</c> or <c>stayTooLong</c>; <c>null</c> otherwise.</summary>
    public string? InvalidReason { get; set; }

    /// <summary>The nights asked for (0 when the departure is not after the arrival).</summary>
    public int Nights { get; set; }

    /// <summary>The minimum stay of the property, when it has one: a shorter stay is <c>belowMinimum</c>, which the host may go on with.</summary>
    public int? MinNights { get; set; }

    /// <summary>What takes the nights, by arrival day; with <c>linkableBlock</c>, the block itself.</summary>
    public List<HostAvailabilityConflictDto> Conflicts { get; set; } = [];

    /// <summary>With <c>occupied</c>: a free stay of the same number of nights nearest to the arrival asked (40 days either way, never before today); <c>null</c> when there is none.</summary>
    public HostAvailabilityWindowDto? NearestFreeWindow { get; set; }

    /// <summary>With <c>linkableBlock</c>: the block of the portal's calendar to complete with <c>POST /api/bookings/manual</c> (<c>blockId</c>).</summary>
    public Guid? LinkableBlockId { get; set; }

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
    };

    // The names the contract uses (lowerCamelCase): the enum member names, whatever the JSON options say about enums.
    private static string Name<TEnum>(TEnum value) where TEnum : struct, Enum => JsonNamingPolicy.CamelCase.ConvertName(value.ToString());

    internal static string ConflictType(HostAvailabilityConflictType type) => Name(type);
}

/// <summary>One thing that takes a night of the dates asked for.</summary>
public sealed class HostAvailabilityConflictDto
{
    public Guid Id { get; set; }

    /// <summary><c>booking</c> (a stay of CasaZen), <c>ical</c> (a reservation a portal sent as a block of its calendar) or <c>block</c> (the host's own).</summary>
    public string Type { get; set; } = string.Empty;

    /// <summary>The guest's name for a booking (the host sees it), the name of the calendar for a portal, the host's note for a block; <c>null</c> when there is none.</summary>
    public string? Title { get; set; }

    /// <summary>Where a booking came from or whose calendar the block is: <c>Direct</c>, <c>Manual</c>, <c>Airbnb</c>, <c>BookingCom</c> or <c>Other</c>; <c>null</c> for the host's own block.</summary>
    public string? Channel { get; set; }

    /// <summary>Arrival day.</summary>
    public DateOnly From { get; set; }

    /// <summary>Departure day, the first day it no longer takes a night.</summary>
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

/// <summary>A free stay of the same number of nights as the one asked for.</summary>
public sealed record HostAvailabilityWindowDto(DateOnly From, DateOnly To);
