namespace Casazen.Core.Entities.Enums;

/// <summary>
/// The role of a person inside an org (<see cref="OrgMember.Role"/>, AM-01). It decides which context memberships
/// the person holds (<see cref="Casazen.Core.Authorization.OrgRoleCatalog"/>), hence what they may do.
/// Persisted as int, explicit values: append-only, never reorder or reuse a value.
/// </summary>
public enum OrgRole
{
    /// <summary>The owner of the org: pays the plan, manages the team. One per org; not transferable in this version (D15).</summary>
    Owner = 1,

    /// <summary>Administers the org like the owner (team, billing, settings), without closing the account or holding the ownership.</summary>
    Admin = 2,

    /// <summary>Runs the properties (bookings, guests, payments, leases) and the suppliers; no billing, no team.</summary>
    PropertyManager = 3,

    /// <summary>Reads properties and bookings and handles guests; cannot change prices, bookings or payments.</summary>
    Collaborator = 4,

    /// <summary>Reads properties, bookings, payments and leases and the org's invoices; writes nothing.</summary>
    Accountant = 5,
}
