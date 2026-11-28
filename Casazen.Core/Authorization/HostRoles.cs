namespace Casazen.Core.Authorization;

public static class HostRoles
{
    public static readonly IReadOnlySet<string> OrgWide =
        new HashSet<string>(["PropertyManager", "Admin"], StringComparer.Ordinal);
}
