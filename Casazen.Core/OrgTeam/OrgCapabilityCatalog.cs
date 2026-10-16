using Casazen.Core.Authorization;

namespace Casazen.Core.OrgTeam;

/// <summary>
/// What a role can do with one capability of the matrix (AM-02c): <see cref="Yes"/> (sees and changes), <see cref="Read"/>
/// (sees, changes nothing) or <see cref="No"/>. There is no «only his properties» level: which properties a person reaches
/// is a matter of the person (<c>OrgMember.PropertyScope</c>), not of the role. Serialized as the member name.
/// </summary>
public enum OrgCapabilityLevel
{
    No = 0,
    Read = 1,
    Yes = 2,
}

/// <summary>The groups of the matrix, in the order the screen draws them. The values are the keys the client translates.</summary>
public static class OrgCapabilityGroups
{
    public const string ShortRent = "short-rent";
    public const string LongRent = "long-rent";

    /// <summary>Valid in either rental area (the interventions of the suppliers).</summary>
    public const string Both = "both";

    public const string Account = "account";
}

/// <summary>A permission as it is held: in the context of a role (<c>short-rent</c>, <c>long-rent</c>, <c>account</c>).</summary>
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

/// <summary>The level of one capability for one role.</summary>
public sealed record OrgCapabilityGrant(string Id, string Group, OrgCapabilityLevel Level);

/// <summary>
/// The matrix «chi può fare cosa» (AM-02c, <c>GET /api/orgs/me/roles</c>): twelve capabilities with stable ids, and the rule
/// that gives each role its level <b>from the permissions of the seeded roles</b> — the same rows the policies of the API
/// read — so the matrix is a reading of the authorization and never a second table that could drift from it. The words that
/// explain each capability are the client's (i18n); nothing here is a sentence.
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
    private const string Account = "account";

    private static ContextPermission P(string context, string permission) => new(context, permission);

    private static readonly OrgCapability[] Entries =
    [
        // ─── Short-term rental ───────────────────────────────────────────────────────────────────────────
        new("booking", OrgCapabilityGroups.ShortRent,
            Full: [P(Short, "booking.write")],
            ReadOnly: [P(Short, "booking.read")],
            Related: [P(Short, "ota.read"), P(Short, "ota.write")]),
        new("prices", OrgCapabilityGroups.ShortRent,
            Full: [P(Short, "property.write")],
            ReadOnly: [P(Short, "property.read")],
            Related: []),
        new("guests", OrgCapabilityGroups.ShortRent,
            Full: [P(Short, "guest.write")],
            ReadOnly: [P(Short, "guest.read")],
            Related: [P(Short, HostPermissions.GuestManage), P(Short, HostPermissions.AlloggiatiSubmit)]),
        new("money", OrgCapabilityGroups.ShortRent,
            Full: [P(Short, "payment.write")],
            ReadOnly: [P(Short, "payment.read")],
            Related: []),

        // ─── Long-term rental ────────────────────────────────────────────────────────────────────────────
        new("leases", OrgCapabilityGroups.LongRent,
            Full: [P(Long, "lease.create")],
            ReadOnly: [P(Long, "lease.read")],
            Related: [P(Long, "lease.sign"), P(Long, "lease.register")]),

        // The list and the register of the rents are behind lease.read (LR-01), the reminders ask for lease.create: so the
        // accountant reads them and the collaborator, who has property.read only, does not.
        new("rents", OrgCapabilityGroups.LongRent,
            Full: [P(Long, "rent.manage"), P(Long, "lease.create")],
            ReadOnly: [P(Long, "lease.read"), P(Long, "rent.read")],
            Related: []),

        // ─── Both areas ──────────────────────────────────────────────────────────────────────────────────
        new("jobs", OrgCapabilityGroups.Both,
            Full: [P(Short, HostPermissions.ServiceRequestWrite), P(Long, "property.write")],
            ReadOnly: [P(Short, "property.read"), P(Long, "property.read")],
            Related: []),

        // ─── The account ─────────────────────────────────────────────────────────────────────────────────
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

    /// <summary>The twelve capabilities, in the order of the screen.</summary>
    public static IReadOnlyList<OrgCapability> All => Entries;

    /// <summary>Every permission (with its context) the matrix knows: the levels read it, the related ones only acknowledge it.</summary>
    public static IReadOnlySet<ContextPermission> Claims { get; } = Entries
        .SelectMany(c => c.Full.Concat(c.ReadOnly).Concat(c.Related))
        .ToHashSet();

    /// <summary>
    /// The level a role with <paramref name="permissionsByContext"/> (context key to the permissions it holds there) has on
    /// <paramref name="capability"/>. A context the role has no row in holds nothing.
    /// </summary>
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

    /// <summary>The level of every capability for a role with <paramref name="permissionsByContext"/>, in the order of <see cref="All"/>.</summary>
    public static IReadOnlyList<OrgCapabilityGrant> LevelsFor(IReadOnlyDictionary<string, IReadOnlyCollection<string>> permissionsByContext) =>
        Entries.Select(c => new OrgCapabilityGrant(c.Id, c.Group, LevelOf(c, permissionsByContext))).ToList();

    private static bool Holds(IReadOnlyDictionary<string, IReadOnlyCollection<string>> permissionsByContext, ContextPermission permission) =>
        permissionsByContext.TryGetValue(permission.ContextKey, out var held)
        && held.Contains(permission.PermissionKey, StringComparer.Ordinal);
}
