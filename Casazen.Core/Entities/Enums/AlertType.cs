namespace Casazen.Core.Entities.Enums;

/// <summary>
/// What a popup alert is about (ADR-025). Stored by name in <see cref="PopupAlert.AlertType"/> and sent to the client by name, so a
/// member can be added at the end later without changing the meaning of the rows already written. The client picks the title and the
/// "Do now" route of an alert from this value; the alert itself carries no text.
/// </summary>
public enum AlertType
{
    /// <summary>A booking waits for the host's decision.</summary>
    BookingNeedsReview,

    /// <summary>A check-in that still has to be completed.</summary>
    CheckInPending,

    /// <summary>A check-out that still has to be completed.</summary>
    CheckOutPending,

    /// <summary>The Alloggiati Web communication is due: a legal deadline, it overrides the quiet hours.</summary>
    AlloggiatiDeadline,

    /// <summary>The ISTAT communication is due: a legal deadline, it overrides the quiet hours.</summary>
    IstatDeadline,

    /// <summary>A property or a stay has a compliance problem to fix.</summary>
    ComplianceIssue,

    /// <summary>A channel stopped syncing with CasaZen.</summary>
    ChannelSyncError,
}
