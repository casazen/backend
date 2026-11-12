namespace Casazen.Core.Entities.Enums;

/// <summary>
/// Where an owner stay stands (E07/F07/US01/T01, NF-111). An owner stay is never a guest booking: it starts as a request and blocks the dates only once
/// approved. Stored as an integer; append-only, a number is never reused.
/// </summary>
public enum OwnerStayStatus
{
    /// <summary>Asked by the owner (or by a contact the owner delegated), waiting for the agency or for the automatic approval of its rule. Blocks nothing.</summary>
    Requested = 1,

    /// <summary>Approved: its availability block exists in CasaZen and goes to the channel manager through the calendar changes.</summary>
    Approved = 2,

    /// <summary>Refused by the agency. Blocks nothing.</summary>
    Rejected = 3,

    /// <summary>Withdrawn by the owner or cancelled by the agency; an approved stay gives its own block back, and only that one.</summary>
    Cancelled = 4,
}

/// <summary>
/// What the channel manager says of the availability block of an owner stay, or of its release (E07/F07/US01/T01). Derived from the calendar change
/// that carries it (<see cref="CalendarChangeState"/>): «acknowledged» is a change kept in CasaZen only (the property is not on the channel manager) or
/// accepted by it; never shown as confirmed before that. Stored as an integer.
/// </summary>
public enum OwnerStaySyncState
{
    /// <summary>Nothing to synchronize (no block yet, or no release yet).</summary>
    None = 0,

    /// <summary>Recorded, the channel manager has not accepted it yet.</summary>
    Pending = 1,

    /// <summary>Kept in CasaZen (the property is not on the channel manager) or accepted by the channel manager.</summary>
    Acknowledged = 2,

    /// <summary>The channel manager refused it, or it could not be sent: a «To do» of the agency until it is sent again.</summary>
    Failed = 3,
}

/// <summary>
/// What a person of the owner portal may do for an owner profile (E07/F07/US01/T02, AC-NF111-4). The owner holds all of them on its own ownerships; a
/// delegated contact holds only the ones the owner gave it, each independent of the others. Stored as an integer mask: append-only, a bit is never reused.
/// </summary>
[Flags]
public enum OwnerPortalPermission
{
    /// <summary>Nothing.</summary>
    None = 0,

    /// <summary><c>calendar.read</c>: the privacy-safe calendar and the owner stays.</summary>
    CalendarRead = 1,

    /// <summary><c>stays.request</c>: ask for an owner stay and withdraw it.</summary>
    StaysRequest = 2,

    /// <summary><c>accounting.read</c>: the published statements, and the documents of type statement or invoice.</summary>
    AccountingRead = 4,

    /// <summary><c>documents.read</c>: the documents the agency shares.</summary>
    DocumentsRead = 8,

    /// <summary><c>inbox.use</c>: the conversations with the agency.</summary>
    InboxUse = 16,
}
