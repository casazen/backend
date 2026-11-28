using Casazen.Core.Entities.Enums;
using Casazen.Core.OrgTeam;

namespace Casazen.Web.DTOs.Orgs;

public class OrgRolesResponse
{
    public IReadOnlyList<OrgRoleDto> Roles { get; set; } = [];

    public static OrgRolesResponse From(IReadOnlyList<OrgRoleView> roles) => new()
    {
        Roles = roles.Select(OrgRoleDto.From).ToList(),
    };
}

public class OrgRoleDto
{
    public OrgRole Role { get; set; }

    public bool Assignable { get; set; }

    public bool ScopeSupported { get; set; }

    public IReadOnlyDictionary<string, OrgRoleContextPermissionsDto> PermissionsByContext { get; set; } =
        new Dictionary<string, OrgRoleContextPermissionsDto>();

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

public class OrgRoleContextPermissionsDto
{
    public string RoleKey { get; set; } = string.Empty;
    public IReadOnlyList<string> Permissions { get; set; } = [];
}

public class OrgCapabilityDto
{
    public string Id { get; set; } = string.Empty;

    public string Group { get; set; } = string.Empty;

    public OrgCapabilityLevel Level { get; set; }

    public static OrgCapabilityDto From(OrgCapabilityGrant grant) => new() { Id = grant.Id, Group = grant.Group, Level = grant.Level };
}
