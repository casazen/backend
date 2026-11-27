using Casazen.Core.Entities;
using Casazen.Core.Services;

namespace Casazen.Web.DTOs;

/// <summary>
/// The steps of the stays of the caller's organization (casazen/backend#978): which ones CasaZen does by itself (<c>Automatic</c>) and which ones the host does
/// (<c>Manual</c>), with the parameter a step needs. Enums by name.
/// </summary>
public sealed class BookingFlowSettingsDto
{
    /// <summary>The online check-in link.</summary>
    public BookingFlowMode CheckInLinkMode { get; init; }

    /// <summary>Days before the arrival the link goes, when automatic (0 = the arrival day).</summary>
    public int CheckInLinkDaysBefore { get; init; }

    /// <summary>The most days allowed: the life of a link, so that it is still open on the arrival day.</summary>
    public int MaxCheckInLinkDaysBefore { get; init; }

    /// <summary>The arrival: registered once the arrival day is over, unless the host marked a no-show.</summary>
    public BookingFlowMode ArrivalMode { get; init; }

    /// <summary>The departure: registered at the check-out time of the property.</summary>
    public BookingFlowMode DepartureMode { get; init; }

    /// <summary>The property ready for the next guest.</summary>
    public BookingFlowMode PropertyReadyMode { get; init; }

    /// <summary>When CasaZen declares the property ready: <c>CleaningDone</c> or <c>CheckOut</c>.</summary>
    public PropertyReadyTrigger PropertyReadyTrigger { get; init; }

    /// <summary>The org never saved its settings: these are the defaults.</summary>
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

/// <summary>A change of the steps of the stays: a member left out keeps its value.</summary>
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
