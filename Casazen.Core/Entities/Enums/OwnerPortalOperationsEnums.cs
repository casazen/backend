namespace Casazen.Core.Entities.Enums;

public enum OwnerStayStatus
{
    Requested = 1,

    Approved = 2,

    Rejected = 3,

    Cancelled = 4,
}

/// <summary>
/// What the channel manager says of the availability block of an owner stay, or of its release (E07/F07/US01/T01). Derived from the calendar change
/// that carries it (<see cref="CalendarChangeState"/>): «acknowledged» is a change kept in CasaZen only (the property is not on the channel manager) or
/// accepted by it; never shown as confirmed before that. Stored as an integer.
/// </summary>
public enum OwnerStaySyncState
{
    None = 0,

    Pending = 1,

    Acknowledged = 2,

    Failed = 3,
}

[Flags]
public enum OwnerPortalPermission
{
    None = 0,

    CalendarRead = 1,

    StaysRequest = 2,

    AccountingRead = 4,

    DocumentsRead = 8,

    InboxUse = 16,
}

public enum OwnerDocumentType
{
    Mandate = 1,

    Contract = 2,

    Insurance = 3,

    Statement = 4,

    Invoice = 5,

    Other = 6,
}

public enum OwnerPortalContactStatus
{
    Invited = 1,

    Active = 2,

    Revoked = 3,
}

public enum OwnerConversationTopic
{
    General = 1,

    Property = 2,

    OwnerStay = 3,

    Document = 4,

    StatementVersion = 5,
}

public enum OwnerMessageSender
{
    Agency = 1,

    Owner = 2,
}
