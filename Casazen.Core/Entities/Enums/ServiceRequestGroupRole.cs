namespace Casazen.Core.Entities.Enums;

/// <summary>
/// What a service request is in its group of backup suppliers (<see cref="ServiceRequest.GroupRole"/>). Stored as an integer:
/// never reorder or renumber, only append. It is a role, not a status: <see cref="ServiceRequestStatus"/> keeps its seven values
/// (the mobile app does not know new ones).
/// </summary>
public enum ServiceRequestGroupRole
{
    /// <summary>The request is not part of a group: it was sent to one supplier only.</summary>
    None = 0,

    /// <summary>
    /// The request to the supplier the host chose. It is the one the host sees and follows, until a backup takes the job in its
    /// place.
    /// </summary>
    Primary = 1,

    /// <summary>
    /// A request to a backup, declared as such to the supplier: a copy of the primary's with the same data. It stays
    /// <see cref="ServiceRequestStatus.Richiesto"/>, holds no slot of the supplier's agenda and is hidden from the host's lists. The
    /// work is its supplier's only if the primary does not take it.
    /// </summary>
    Backup = 2,

    /// <summary>
    /// The primary of a group whose job a backup took: it did not answer in time, so the request is cancelled (<c>NoResponse</c>).
    /// It leaves the host's lists to the backup that replaced it (<see cref="PromotedBackup"/>).
    /// </summary>
    ReplacedPrimary = 3,

    /// <summary>
    /// The backup that took the job of the group (the first to accept when the primary did not). From then on it is a request
    /// like any other: the host sees it, follows it and pays it.
    /// </summary>
    PromotedBackup = 4,
}
