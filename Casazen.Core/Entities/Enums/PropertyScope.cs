namespace Casazen.Core.Entities.Enums;

/// <summary>
/// Which properties of the org a member reaches (<see cref="OrgMember.PropertyScope"/>, AM-01). Every member is
/// <see cref="All"/> until the per-property scope exists (AM-03: <c>Selected</c> means the rows of
/// <c>PropertyMemberAccess</c>). Persisted as int, explicit values: append-only.
/// </summary>
public enum PropertyScope
{
    /// <summary>Every property of the org (the owner always has this scope).</summary>
    All = 1,

    /// <summary>Only the properties granted one by one (AM-03).</summary>
    Selected = 2,
}
