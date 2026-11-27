using Casazen.Core.Authorization;
using Casazen.Core.Entities.Enums;

namespace Casazen.Core.OrgTeam;

public sealed record OrgRoleContextPermissions(string RoleKey, IReadOnlyList<string> Permissions);

/// <summary>One org role as the matrix describes it (<see cref="OrgRoleMatrix.Build"/>).</summary>
/// <param name="Role">The org role.</param>
/// <param name="Assignable">The <b>caller</b> may give this role (invitation, change of role): <see cref="OrgTeamRules.CanAssign"/>. Nobody gives the owner; only the owner gives the administrator.</param>
/// <param name="ScopeSupported">The role can be limited to some properties («Solo alcuni»): the collaborator only (decision AM-D16).</param>
/// <param name="PermissionsByContext">What the role holds in each context it has a row in: <c>account</c>, <c>short-rent</c>, <c>long-rent</c>.</param>
/// <param name="Capabilities">The twelve capabilities with the level of this role, in the order of <see cref="OrgCapabilityCatalog.All"/>.</param>
public sealed record OrgRoleView(
    OrgRole Role,
    bool Assignable,
    bool ScopeSupported,
    IReadOnlyDictionary<string, OrgRoleContextPermissions> PermissionsByContext,
    IReadOnlyList<OrgCapabilityGrant> Capabilities);

public static class OrgRoleMatrix
{
    public static IReadOnlyList<string> Contexts { get; } = [AccountContext.Key, OrgRoleCatalog.ShortRent, OrgRoleCatalog.LongRent];

    public static IReadOnlyList<OrgRoleView> Build(
        IReadOnlyDictionary<(string ContextKey, string RoleKey), IReadOnlyCollection<string>> seeded,
        OrgRole caller)
    {
        ArgumentNullException.ThrowIfNull(seeded);

        return Enum.GetValues<OrgRole>().Select(role => Describe(role, seeded, caller)).ToList();
    }

    private static OrgRoleView Describe(
        OrgRole role,
        IReadOnlyDictionary<(string ContextKey, string RoleKey), IReadOnlyCollection<string>> seeded,
        OrgRole caller)
    {
        var byContext = new Dictionary<string, OrgRoleContextPermissions>(StringComparer.Ordinal);
        var held = new Dictionary<string, IReadOnlyCollection<string>>(StringComparer.Ordinal);

        foreach (var context in Contexts)
        {
            var roleKey = AccountContext.IsAccountContext(context)
                ? OrgRoleCatalog.AccountRoleKey(role)
                : OrgRoleCatalog.HostRoleKey(role, context);

            if (roleKey is null || !seeded.TryGetValue((context, roleKey), out var permissions))
                continue;

            var ordered = permissions.Order(StringComparer.Ordinal).ToList();
            byContext[context] = new OrgRoleContextPermissions(roleKey, ordered);
            held[context] = ordered;
        }

        return new OrgRoleView(
            role,
            OrgTeamRules.CanAssign(caller, role),
            role == OrgRole.Collaborator,
            byContext,
            OrgCapabilityCatalog.LevelsFor(held));
    }
}
