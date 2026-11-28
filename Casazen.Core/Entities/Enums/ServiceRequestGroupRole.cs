namespace Casazen.Core.Entities.Enums;

/// <summary>
/// What a service request is in its group of backup suppliers (<see cref="ServiceRequest.GroupRole"/>). Stored as an integer:
/// never reorder or renumber, only append. It is a role, not a status: <see cref="ServiceRequestStatus"/> keeps its seven values
/// (the mobile app does not know new ones).
/// </summary>
public enum ServiceRequestGroupRole
{
    None = 0,

    Primary = 1,

    Backup = 2,

    ReplacedPrimary = 3,

    PromotedBackup = 4,
}
