namespace Casazen.Core.Entities.Enums;

/// <summary>
/// Campaign lifecycle status (NF-24, ADR-014).
/// </summary>
public enum CampaignStatus
{
    /// <summary>Initial state: not yet submitted for approval.</summary>
    Draft,

    /// <summary>Submitted and awaiting approval.</summary>
    PendingApproval,

    /// <summary>Approved and ready to schedule.</summary>
    Approved,

    /// <summary>Scheduled for future send.</summary>
    Scheduled,

    /// <summary>Send completed.</summary>
    Sent
}
