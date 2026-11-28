namespace Casazen.Core.Entities.Enums;

public enum AlertType
{
    BookingNeedsReview,

    CheckInPending,

    CheckOutPending,

    /// <summary>The Alloggiati Web communication is due: a legal deadline, it overrides the quiet hours.</summary>
    AlloggiatiDeadline,

    /// <summary>The ISTAT communication is due: a legal deadline, it overrides the quiet hours.</summary>
    IstatDeadline,

    ComplianceIssue,

    ChannelSyncError,
}
