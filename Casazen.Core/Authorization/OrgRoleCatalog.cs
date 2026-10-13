using Casazen.Core.Entities.Enums;

namespace Casazen.Core.Authorization;

/// <summary>A role of a context (<c>Roles.ContextKey</c> and <c>RoleKey</c>) that a membership row points to.</summary>
public sealed record ProjectedRole(string ContextKey, string RoleKey);

/// <summary>
/// A role row seeded by the migration <c>AddOrgMembership</c> (<c>HasData</c>), with the permissions it holds.
/// </summary>
public sealed record SeededRole(int Id, string ContextKey, string RoleKey, IReadOnlyList<string> Permissions);

/// <summary>
/// What an <see cref="OrgRole"/> means in terms of context memberships and permissions (AM-01), and the role rows that
/// carry them. The single place that says it: the membership service writes the projection from it, the reconcile
/// command re-aligns the projection to it, the model seeds the roles from it and the tests check the three agree
/// (<c>docs/runbooks/org-team.md</c>).
/// </summary>
/// <remarks>
/// <para>A person gets <b>one membership per context</b> (unique <c>(UserId, ContextKey)</c>): the role of the context
/// the org role maps to. The account context is given to the roles that administer or read the org
/// (<see cref="AccountContext"/>); the rental contexts (<see cref="RentalContexts"/>) are the <i>areas</i> the person
/// works in, chosen when they are added and kept as the contexts of their memberships.</para>
/// <para>Org role to role keys:</para>
/// <list type="table">
/// <listheader><term>Org role</term><description>account / short-rent / long-rent</description></listheader>
/// <item><term>Owner</term><description><c>org_owner</c> / <c>property_owner</c> / <c>long_term_landlord</c> (the owner's host rows come from the onboarding, see <c>IUserContextMembershipService</c>)</description></item>
/// <item><term>Admin</term><description><c>org_admin</c> / <c>property_manager</c> / <c>property_manager</c></description></item>
/// <item><term>PropertyManager</term><description>none / <c>property_manager</c> / <c>property_manager</c></description></item>
/// <item><term>Collaborator</term><description>none / <c>staff</c> / <c>staff</c> (long-rent: <c>property.read</c> only)</description></item>
/// <item><term>Accountant</term><description><c>org_accountant</c> / <c>accountant</c> / <c>accountant</c></description></item>
/// </list>
/// <para>AM-03 splits three things off the broad permissions, so that the collaborator can do its job without prices, CIN or
/// bookings: <see cref="HostPermissions.ServiceRequestWrite"/> (create a request to a supplier, mark it paid: it used to be
/// <c>property.write</c>), <see cref="HostPermissions.GuestManage"/> (erase, anonymize and change the consents of a guest: it
/// used to be <c>guest.write</c>) and <see cref="HostPermissions.AlloggiatiSubmit"/> (register the guests of a stay and declare
/// the Alloggiati communication sent: it used to be <c>booking.write</c>). The collaborator gets the first only; the owner and
/// the property managers get all three, so nothing changes for them.</para>
/// </remarks>
public static class OrgRoleCatalog
{
    public const string ShortRent = "short-rent";
    public const string LongRent = "long-rent";

    /// <summary>Role key of the owner of the short-rent context (<c>property_owner</c>).</summary>
    public const string ShortRentOwnerRoleKey = "property_owner";

    /// <summary>Role key of the owner of the long-rent context (<c>long_term_landlord</c>).</summary>
    public const string LongRentOwnerRoleKey = "long_term_landlord";

    public const string PropertyManagerRoleKey = "property_manager";
    public const string StaffRoleKey = "staff";
    public const string AccountantRoleKey = "accountant";

    /// <summary>The contexts a person works in (the <i>areas</i> they are added to).</summary>
    public static IReadOnlyList<string> RentalContexts { get; } = [ShortRent, LongRent];

    public static bool IsRentalContext(string contextKey) =>
        RentalContexts.Contains(contextKey, StringComparer.OrdinalIgnoreCase);

    /// <summary>The role key of <paramref name="role"/> in the account context; <c>null</c> when the role has no account membership.</summary>
    public static string? AccountRoleKey(OrgRole role) => role switch
    {
        OrgRole.Owner => AccountContext.RoleKeys.Owner,
        OrgRole.Admin => AccountContext.RoleKeys.Admin,
        OrgRole.Accountant => AccountContext.RoleKeys.Accountant,
        _ => null,
    };

    /// <summary>The role key of <paramref name="role"/> in the rental context <paramref name="rentalContext"/>.</summary>
    public static string HostRoleKey(OrgRole role, string rentalContext)
    {
        if (!IsRentalContext(rentalContext))
            throw new ArgumentException($"'{rentalContext}' is not a rental context.", nameof(rentalContext));

        return role switch
        {
            OrgRole.Owner => string.Equals(rentalContext, ShortRent, StringComparison.OrdinalIgnoreCase)
                ? ShortRentOwnerRoleKey
                : LongRentOwnerRoleKey,
            OrgRole.Admin or OrgRole.PropertyManager => PropertyManagerRoleKey,
            OrgRole.Collaborator => StaffRoleKey,
            OrgRole.Accountant => AccountantRoleKey,
            _ => throw new ArgumentOutOfRangeException(nameof(role), role, "Unknown org role"),
        };
    }

    /// <summary>
    /// The membership rows <paramref name="role"/> implies for a person who works in <paramref name="rentalContexts"/>:
    /// the account row of the role, if it has one, then one row per rental context.
    /// </summary>
    public static IReadOnlyList<ProjectedRole> ProjectionOf(OrgRole role, IEnumerable<string> rentalContexts)
    {
        var projection = new List<ProjectedRole>();
        if (AccountRoleKey(role) is { } accountKey)
            projection.Add(new ProjectedRole(AccountContext.Key, accountKey));

        foreach (var context in RentalContexts.Where(c => rentalContexts.Contains(c, StringComparer.OrdinalIgnoreCase)))
            projection.Add(new ProjectedRole(context, HostRoleKey(role, context)));

        return projection;
    }

    private static readonly string[] ShortRentOperational =
    [
        "property.read", "property.write", "booking.read", "booking.write", "payment.read", "payment.write",
        "ota.read", "ota.write", "guest.read", "guest.write",
        .. HostPermissions.ShortRentFine,
    ];

    private static readonly string[] LongRentOperational =
    [
        "property.read", "property.write", "lease.read", "lease.create", "lease.sign", "lease.register",
        "rent.read", "rent.manage",
    ];

    /// <summary>
    /// The role rows added by the migration <c>AddOrgMembership</c> (ids 4 to 12; 1 to 3 are the owner's and the platform
    /// admin's, seeded since the context authorization). Ids are explicit because the model seeds them.
    /// </summary>
    public static IReadOnlyList<SeededRole> SeededRoles { get; } =
    [
        new(4, AccountContext.Key, AccountContext.RoleKeys.Owner, AccountContext.Permissions.All),
        new(5, AccountContext.Key, AccountContext.RoleKeys.Admin, AccountContext.Permissions.All),
        new(6, AccountContext.Key, AccountContext.RoleKeys.Accountant, [AccountContext.Permissions.BillingRead]),

        // Property manager: everything operational, plus the org's trusted suppliers; no billing, no team.
        new(7, ShortRent, PropertyManagerRoleKey, [.. ShortRentOperational, AccountContext.Permissions.SuppliersManage]),
        new(8, LongRent, PropertyManagerRoleKey, [.. LongRentOperational, AccountContext.Permissions.SuppliersManage]),

        // Collaborator: reads properties and bookings, handles guests and asks the suppliers for interventions (AM-03); no
        // prices, CIN, bookings, payments, no erasure of guests, no Alloggiati declaration. In long-term only the property list.
        new(9, ShortRent, StaffRoleKey,
            ["property.read", "booking.read", "guest.read", "guest.write", HostPermissions.ServiceRequestWrite]),
        new(10, LongRent, StaffRoleKey, ["property.read"]),

        // Accountant: reads, never writes. Its invoices are in the account context (org.billing.read).
        new(11, ShortRent, AccountantRoleKey, ["property.read", "booking.read", "payment.read"]),
        new(12, LongRent, AccountantRoleKey, ["property.read", "lease.read"]),
    ];
}
