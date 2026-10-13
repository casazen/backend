namespace Casazen.Core.Authorization;

/// <summary>
/// Role names that decide the reach of a host inside its own org. Single source for the resource handler, the list
/// scopes (<see cref="HostScope"/>) and the legacy <c>IPropertyAuthorizationService</c>: services never list roles.
/// </summary>
public static class HostRoles
{
    /// <summary>
    /// Roles that see every property of their org. Everyone else (a <c>PropertyOwner</c>, a collaborator with only a
    /// DB membership) reaches only the properties they own. The org boundary always applies on top.
    /// </summary>
    public static readonly IReadOnlySet<string> OrgWide =
        new HashSet<string>(["PropertyManager", "Admin"], StringComparer.Ordinal);
}
