using Casazen.Core.Entities.Enums;
using Casazen.Core.OrgTeam;

namespace Casazen.Web.DTOs.Orgs;

// The matrix of the org roles (AM-02c). Enums travel as member names (the API's single enum converter). Nothing here is a
// sentence: the ids of the capabilities are stable keys and the words are the client's (i18n).

/// <summary><c>GET /api/orgs/me/roles</c>: the five org roles, owner first.</summary>
public class OrgRolesResponse
{
    public IReadOnlyList<OrgRoleDto> Roles { get; set; } = [];

    public static OrgRolesResponse From(IReadOnlyList<OrgRoleView> roles) => new()
    {
        Roles = roles.Select(OrgRoleDto.From).ToList(),
    };
}

/// <summary>One org role: what it holds in each context, how much of each capability that is, and what the caller may do with it.</summary>
public class OrgRoleDto
{
    /// <summary><c>Owner</c>, <c>Admin</c>, <c>PropertyManager</c>, <c>Collaborator</c> or <c>Accountant</c>.</summary>
    public OrgRole Role { get; set; }

    /// <summary>
    /// The <b>caller</b> may give this role to a person (an invitation, a change of role). The owner role is given to nobody; the
    /// administrator role only by the owner; the other roles by the owner and the administrators; nothing by anybody else.
    /// </summary>
    public bool Assignable { get; set; }

    /// <summary>The role can be limited to some properties («Solo alcuni», <c>PropertyScope</c>): the collaborator only.</summary>
    public bool ScopeSupported { get; set; }

    /// <summary>
    /// What the role holds in the contexts <c>account</c>, <c>short-rent</c> and <c>long-rent</c> (the shape of
    /// <c>GET /api/me/contexts</c>): the role key and the permissions. A context the role has no row in is absent.
    /// </summary>
    public IReadOnlyDictionary<string, OrgRoleContextPermissionsDto> PermissionsByContext { get; set; } =
        new Dictionary<string, OrgRoleContextPermissionsDto>();

    /// <summary>The twelve capabilities of the matrix, always in the same order, with the level of this role.</summary>
    public IReadOnlyList<OrgCapabilityDto> Capabilities { get; set; } = [];

    public static OrgRoleDto From(OrgRoleView view) => new()
    {
        Role = view.Role,
        Assignable = view.Assignable,
        ScopeSupported = view.ScopeSupported,
        PermissionsByContext = view.PermissionsByContext.ToDictionary(
            c => c.Key,
            c => new OrgRoleContextPermissionsDto { RoleKey = c.Value.RoleKey, Permissions = c.Value.Permissions }),
        Capabilities = view.Capabilities.Select(OrgCapabilityDto.From).ToList(),
    };
}

/// <summary>The role key of a role in a context and the permissions it holds there.</summary>
public class OrgRoleContextPermissionsDto
{
    public string RoleKey { get; set; } = string.Empty;
    public IReadOnlyList<string> Permissions { get; set; } = [];
}

/// <summary>A capability of the matrix and the level one role has on it.</summary>
public class OrgCapabilityDto
{
    /// <summary>
    /// Stable key: <c>booking</c>, <c>prices</c>, <c>guests</c>, <c>money</c> (short-term rental); <c>leases</c>, <c>rents</c>
    /// (long-term rental); <c>jobs</c> (both); <c>people</c>, <c>billing</c>, <c>suppliers</c>, <c>settings</c>,
    /// <c>security</c> (the account).
    /// </summary>
    public string Id { get; set; } = string.Empty;

    /// <summary><c>short-rent</c>, <c>long-rent</c>, <c>both</c> or <c>account</c>.</summary>
    public string Group { get; set; } = string.Empty;

    /// <summary><c>Yes</c> (sees and changes), <c>Read</c> (sees only) or <c>No</c>. Which properties is the person's, not the role's.</summary>
    public OrgCapabilityLevel Level { get; set; }

    public static OrgCapabilityDto From(OrgCapabilityGrant grant) => new() { Id = grant.Id, Group = grant.Group, Level = grant.Level };
}
