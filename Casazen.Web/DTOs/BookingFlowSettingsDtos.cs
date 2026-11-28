using Casazen.Core.Entities;
using Casazen.Core.Services;

namespace Casazen.Web.DTOs;

public sealed class BookingFlowSettingsDto
{
    public BookingFlowMode CheckInLinkMode { get; init; }

    public int CheckInLinkDaysBefore { get; init; }

    /// <summary>The most days allowed: the life of a link, so that it is still open on the arrival day.</summary>
    public int MaxCheckInLinkDaysBefore { get; init; }

    /// <summary>The arrival: registered once the arrival day is over, unless the host marked a no-show.</summary>
    public BookingFlowMode ArrivalMode { get; init; }

    public BookingFlowMode DepartureMode { get; init; }

    public BookingFlowMode PropertyReadyMode { get; init; }

    public PropertyReadyTrigger PropertyReadyTrigger { get; init; }

    public bool IsDefault { get; init; }

    public static BookingFlowSettingsDto From(BookingFlowSettingsView view) => new()
    {
        CheckInLinkMode = view.CheckInLinkMode,
        CheckInLinkDaysBefore = view.CheckInLinkDaysBefore,
        MaxCheckInLinkDaysBefore = view.MaxCheckInLinkDaysBefore,
        ArrivalMode = view.ArrivalMode,
        DepartureMode = view.DepartureMode,
        PropertyReadyMode = view.PropertyReadyMode,
        PropertyReadyTrigger = view.PropertyReadyTrigger,
        IsDefault = view.IsDefault,
    };
}

public sealed class UpdateBookingFlowSettingsDto
{
    public BookingFlowMode? CheckInLinkMode { get; init; }

    public int? CheckInLinkDaysBefore { get; init; }

    public BookingFlowMode? ArrivalMode { get; init; }

    public BookingFlowMode? DepartureMode { get; init; }

    public BookingFlowMode? PropertyReadyMode { get; init; }

    public PropertyReadyTrigger? PropertyReadyTrigger { get; init; }

    public BookingFlowSettingsUpdate ToUpdate() => new(
        CheckInLinkMode,
        CheckInLinkDaysBefore,
        ArrivalMode,
        DepartureMode,
        PropertyReadyMode,
        PropertyReadyTrigger);
}
