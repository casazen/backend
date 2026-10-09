namespace Casazen.Core.Entities.Enums;

/// <summary>
/// What the id of an entry of the activity log points to (<see cref="OrgActivityEntry.SubjectType"/>, AM-02b). The log keeps
/// the id and never a name: the client resolves it with what it can already see (the people page, the properties list).
/// Persisted as int, explicit values: append-only, never reorder or reuse a value.
/// </summary>
public enum OrgActivitySubjectType
{
    /// <summary>A person of the org: the id is its account (<c>User.Id</c>), the same id the actor carries.</summary>
    Member = 1,

    /// <summary>An invitation to the org: the id is <c>OrgInvitation.Id</c>.</summary>
    Invitation = 2,

    /// <summary>The org itself: the id is <c>Org.Id</c> (plan, name, slug, a request to the administrators).</summary>
    Org = 3,

    /// <summary>A property: the id is <c>Property.Id</c>.</summary>
    Property = 4,

    /// <summary>A supplier: the id is the supplier's org (<c>Org.Id</c>).</summary>
    Supplier = 5,
}
