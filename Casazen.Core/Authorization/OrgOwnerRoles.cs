namespace Casazen.Core.Authorization;

/// <summary>
/// The DB role keys that make the holder of a <see cref="Casazen.Core.Entities.UserContextMembership"/> the <b>owner</b> of its org
/// (or its administrator, or the platform admin), per context. The one place that answers "is this membership the owner's?":
/// the org billing policy and the onboarding guard both ask it, so a collaborator, a property manager or an accountant (any other role
/// key of the same context) never passes for the owner (AM-00, S1/S2).
/// </summary>
/// <remarks>
/// <para>The owner of either rental context pays the plan of its org (a landlord with only long-term leases too, PL-16): the
/// <c>property_owner</c> of <c>short-rent</c> and the <c>long_term_landlord</c> of <c>long-rent</c>. The platform admin
/// (<c>platform_admin</c> of <c>admin</c>) is admitted because it acts as a host of the org it onboarded. A role key is
/// owner's only <i>in its own context</i>: <c>property_owner</c> in <c>long-rent</c> is not.</para>
/// <para>AM-01 adds the <see cref="AccountContext"/>: its <c>org_owner</c> and its <c>org_admin</c>, the two roles that
/// hold <c>org.billing.manage</c> (the permission the <c>OrgBillingAdmin</c> policy evaluates). A role that gets that
/// permission must be listed here too: <c>OrgOwnerRolesCatalogTests</c> reads the seeded roles and fails when one is
/// missing, so a billing administrator is never forgotten by the policy nor by the onboarding guard.</para>
/// </remarks>
public static class OrgOwnerRoles
{
    private static readonly IReadOnlyDictionary<string, IReadOnlySet<string>> RoleKeysByContext =
        new Dictionary<string, IReadOnlySet<string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["short-rent"] = new HashSet<string>([OrgRoleCatalog.ShortRentOwnerRoleKey], StringComparer.OrdinalIgnoreCase),
            ["long-rent"] = new HashSet<string>([OrgRoleCatalog.LongRentOwnerRoleKey], StringComparer.OrdinalIgnoreCase),
            ["admin"] = new HashSet<string>(["platform_admin"], StringComparer.OrdinalIgnoreCase),
            [AccountContext.Key] = new HashSet<string>(
                [AccountContext.RoleKeys.Owner, AccountContext.RoleKeys.Admin], StringComparer.OrdinalIgnoreCase),
        };

    /// <summary>Every owner role, as <c>(context, role key)</c>: what the tests compare with the seeded roles.</summary>
    public static IEnumerable<(string ContextKey, string RoleKey)> All =>
        RoleKeysByContext.SelectMany(context => context.Value.Select(roleKey => (context.Key, roleKey)));

    /// <summary>True when <paramref name="roleKey"/> is an owner's (or administrator's, or platform admin's) role of <paramref name="contextKey"/>.</summary>
    public static bool IsOwnerRole(string contextKey, string roleKey) =>
        RoleKeysByContext.TryGetValue(contextKey, out var roleKeys) && roleKeys.Contains(roleKey);

    /// <summary>
    /// True for the membership of a <b>member</b> of an org: a host context (<see cref="HostOnboarding.HostContextKeys"/>)
    /// held with a role key that is not the owner's. Contexts that are not host contexts (<c>admin</c>, <c>supplier</c>)
    /// never make a member.
    /// </summary>
    public static bool IsHostMemberRole(string contextKey, string roleKey) =>
        HostOnboarding.IsHostContext(contextKey) && !IsOwnerRole(contextKey, roleKey);
}
