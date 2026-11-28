namespace Casazen.Core.Entities.Enums;

/// <summary>
/// The role of a person inside an org (<see cref="OrgMember.Role"/>, AM-01). It decides which context memberships
/// the person holds (<see cref="Casazen.Core.Authorization.OrgRoleCatalog"/>), hence what they may do.
/// Persisted as int, explicit values: append-only, never reorder or reuse a value.
/// </summary>
public enum OrgRole
{
    Owner = 1,

    Admin = 2,

    PropertyManager = 3,

    Collaborator = 4,

    Accountant = 5,
}
