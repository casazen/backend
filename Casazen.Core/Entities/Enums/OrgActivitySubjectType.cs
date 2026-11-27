namespace Casazen.Core.Entities.Enums;

/// <summary>
/// What the id of an entry of the activity log points to (<see cref="OrgActivityEntry.SubjectType"/>, AM-02b). The log keeps
/// the id and never a name: the client resolves it with what it can already see (the people page, the properties list).
/// Persisted as int, explicit values: append-only, never reorder or reuse a value.
/// </summary>
public enum OrgActivitySubjectType
{
    Member = 1,

    Invitation = 2,

    Org = 3,

    Property = 4,

    Supplier = 5,

    Booking = 6,

    OwnerInvitation = 7,

    OwnerPortalItem = 8,
}
