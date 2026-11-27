using Casazen.Core.Authorization;

namespace Casazen.Core.OrgTeam;

public enum OrgCapabilityLevel
{
    No = 0,
    Read = 1,
    Yes = 2,
}

public static class OrgCapabilityGroups
{
    public const string ShortRent = "short-rent";
    public const string LongRent = "long-rent";

    public const string Both = "both";

    public const string Account = "account";
}

public readonly record struct ContextPermission(string ContextKey, string PermissionKey);

/// <summary>
/// One row of the matrix of the roles (AM-02c): a stable <see cref="Id"/> the client translates (no text here), its
/// <see cref="Group"/>, and the permissions of the seeded roles that decide its level. A role holds the capability fully
/// when it holds any of <see cref="Full"/>, reads it when it holds any of <see cref="ReadOnly"/>, and not at all otherwise.
/// <see cref="Related"/> are permissions that belong to the capability without changing its level (they refine what a role can
/// do inside it): they are listed so that no seeded permission is left without a place in the matrix.
/// </summary>
public sealed record OrgCapability(
    string Id,
    string Group,
    IReadOnlyList<ContextPermission> Full,
    IReadOnlyList<ContextPermission> ReadOnly,
    IReadOnlyList<ContextPermission> Related);

public sealed record OrgCapabilityGrant(string Id, string Group, OrgCapabilityLevel Level);

/// <summary>
/// The matrix «chi può fare cosa» (AM-02c, <c>GET /api/orgs/me/roles</c>): twelve capabilities with stable ids, and the rule
/// that gives each role its level <b>from the permissions of the seeded roles</b> — the same rows the policies of the API
/// read — so the matrix is a reading of the permissions and never a second table that could drift from them. The words that
/// explain each capability are the client's (i18n); nothing here is a sentence. Not every permission has an endpoint that asks
/// for it yet: <c>billing</c> (the endpoints ask for <c>OrgBillingAdmin</c>, owner and administrators), <c>suppliers</c> (AM-10)
/// and <c>settings</c> report the permission rows and have no probe in the coherence test (AM-02c-FU4,
/// <c>docs/runbooks/org-team.md</c> section 47).
/// </summary>
/// <remarks>
/// <para>Expected values (analysis of the area, §2.3, decision D14: the collaborator reads the prices): the owner and the
/// administrators have <see cref="OrgCapabilityLevel.Yes"/> everywhere; the property manager everywhere but people, billing,
/// settings and security; the collaborator reads bookings and prices, handles the guests and asks for interventions; the
/// accountant reads bookings, prices, money, leases, rents, interventions and the invoices.</para>
/// <para>The collaborator's «Yes» on <c>guests</c> and <c>jobs</c> is the narrower one: no erasure of a guest, no declaration of
/// the Alloggiati communication (<c>guest.manage</c>, <c>alloggiati.submit</c> are <see cref="OrgCapability.Related"/>), and no
/// confirmation of a final amount nor online payment of an intervention (it holds <c>servicerequest.write</c> and not
/// <c>property.write</c>). The texts say it; the matrix says «Yes».</para>
/// <para><b>Tripwire.</b> <see cref="Claims"/> lists every permission the matrix knows. A test fails when a seeded role holds a
/// permission outside it: whoever adds a permission to a role must decide in which capability it belongs.</para>
/// </remarks>
public static class OrgCapabilityCatalog
{
    private const string Short = OrgRoleCatalog.ShortRent;
    private const string Long = OrgRoleCatalog.LongRent;
    private const string Account = AccountContext.Key;

    private static ContextPermission P(string context, string permission) => new(context, permission);

    private static readonly OrgCapability[] Entries =
    [
        new("booking", OrgCapabilityGroups.ShortRent,
            Full: [P(Short, "booking.write")],
            ReadOnly: [P(Short, "booking.read")],
            Related:
            [
                P(Short, "ota.read"),
                P(Short, "ota.write"),
                P(Short, HostPermissions.ApprovalDecide),
            ]),
        new("prices", OrgCapabilityGroups.ShortRent,
            Full: [P(Short, "property.write")],
            ReadOnly: [P(Short, "property.read")],
            Related: []),
        new("marketing", OrgCapabilityGroups.ShortRent, Full: [P(Short, HostPermissions.Marketing)], ReadOnly: [], Related: []),
        new("guests", OrgCapabilityGroups.ShortRent,
            Full: [P(Short, "guest.write")],
            ReadOnly: [P(Short, "guest.read")],
            Related:
            [
                P(Short, HostPermissions.GuestManage),
                P(Short, HostPermissions.AlloggiatiSubmit),
                P(Short, HostPermissions.VerificationDecision),
                P(Short, HostPermissions.GuestPortalManage),
            ]),
        new("money", OrgCapabilityGroups.ShortRent,
            Full: [P(Short, "payment.write")],
            ReadOnly: [P(Short, "payment.read")],
            Related: []),

        new("leases", OrgCapabilityGroups.LongRent,
            Full: [P(Long, "lease.create")],
            ReadOnly: [P(Long, "lease.read")],
            Related: [P(Long, "lease.sign"), P(Long, "lease.register")]),

        new("rents", OrgCapabilityGroups.LongRent,
            Full: [P(Long, "rent.manage"), P(Long, "lease.create")],
            ReadOnly: [P(Long, "lease.read"), P(Long, "rent.read")],
            Related: []),

        new("jobs", OrgCapabilityGroups.Both,
            Full: [P(Short, HostPermissions.ServiceRequestWrite), P(Long, "property.write")],
            ReadOnly: [P(Short, "property.read"), P(Long, "property.read")],
            Related: []),

        new("people", OrgCapabilityGroups.Account,
            Full: [P(Account, AccountContext.Permissions.MembersManage)],
            ReadOnly: [],
            Related: []),
        new("billing", OrgCapabilityGroups.Account,
            Full: [P(Account, AccountContext.Permissions.BillingManage)],
            ReadOnly: [P(Account, AccountContext.Permissions.BillingRead)],
            Related: []),
        new("suppliers", OrgCapabilityGroups.Account,
            Full: [P(Account, AccountContext.Permissions.SuppliersManage)],
            ReadOnly: [],
            Related: [P(Short, AccountContext.Permissions.SuppliersManage), P(Long, AccountContext.Permissions.SuppliersManage)]),
        new("settings", OrgCapabilityGroups.Account,
            Full: [P(Account, AccountContext.Permissions.SettingsManage)],
            ReadOnly: [],
            Related: []),
        new("security", OrgCapabilityGroups.Account,
            Full: [P(Account, AccountContext.Permissions.ActivityRead)],
            ReadOnly: [],
            Related: []),
    ];

    public static IReadOnlyList<OrgCapability> All => Entries;

    public static IReadOnlySet<ContextPermission> Claims { get; } = Entries
        .SelectMany(c => c.Full.Concat(c.ReadOnly).Concat(c.Related))
        .ToHashSet();

    public static OrgCapabilityLevel LevelOf(
        OrgCapability capability,
        IReadOnlyDictionary<string, IReadOnlyCollection<string>> permissionsByContext)
    {
        ArgumentNullException.ThrowIfNull(capability);
        ArgumentNullException.ThrowIfNull(permissionsByContext);

        if (capability.Full.Any(p => Holds(permissionsByContext, p)))
            return OrgCapabilityLevel.Yes;

        return capability.ReadOnly.Any(p => Holds(permissionsByContext, p)) ? OrgCapabilityLevel.Read : OrgCapabilityLevel.No;
    }

    public static IReadOnlyList<OrgCapabilityGrant> LevelsFor(IReadOnlyDictionary<string, IReadOnlyCollection<string>> permissionsByContext) =>
        Entries.Select(c => new OrgCapabilityGrant(c.Id, c.Group, LevelOf(c, permissionsByContext))).ToList();

    private static bool Holds(IReadOnlyDictionary<string, IReadOnlyCollection<string>> permissionsByContext, ContextPermission permission) =>
        permissionsByContext.TryGetValue(permission.ContextKey, out var held)
        && held.Contains(permission.PermissionKey, StringComparer.Ordinal);
}
