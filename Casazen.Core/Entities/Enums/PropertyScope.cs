namespace Casazen.Core.Entities.Enums;

/// <summary>
/// Which properties of the org a member reaches (<see cref="OrgMember.PropertyScope"/>, AM-01). <see cref="All"/> is every
/// property, the ones created later too. <see cref="Selected"/> («Solo alcuni», AM-03) is the properties listed in
/// <c>PropertyMemberAccess</c>, none if the list is empty, and is the collaborator's only: any other role reaches the whole
/// org whatever it holds. Persisted as int, explicit values: append-only.
/// </summary>
public enum PropertyScope
{
    /// <summary>Every property of the org (the owner always has this scope).</summary>
    All = 1,

    /// <summary>Only the properties granted one by one (AM-03).</summary>
    Selected = 2,
}
