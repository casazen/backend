namespace Casazen.Core.Authorization;

/// <summary>
/// The <c>account</c> context (AM-01): the «Amministrazione» of the customer's org, where its owner and administrators
/// manage people, billing, settings and suppliers. Not the staff console: the platform's <c>admin</c> context keeps its
/// key, its role and its permissions, and stays reserved to CasaZen staff (wave decision D1).
/// </summary>
/// <remarks>
/// It is a <b>host</b> context (<see cref="HostOnboarding.HostContextKeys"/>): like <c>short-rent</c> and
/// <c>long-rent</c> it waits for the onboarding and the current consents. Its memberships exist only as the projection of
/// an <see cref="Casazen.Core.Entities.OrgMember"/> (<see cref="OrgRoleCatalog"/>): no JWT role maps to it, so a token
/// can never grant it.
/// </remarks>
public static class AccountContext
{
    /// <summary>Primary key of the context (<c>AppContexts.Key</c>), <c>contextKey</c> of <c>GET /api/me/contexts</c>.</summary>
    public const string Key = "account";

    /// <summary>Name shown to the customer.</summary>
    public const string DisplayName = "Amministrazione";

    /// <summary>Home of the context in the web app (the screens arrive with AM-04).</summary>
    public const string DefaultRoute = "/app/account";

    public static bool IsAccountContext(string contextKey) =>
        string.Equals(contextKey, Key, StringComparison.OrdinalIgnoreCase);

    /// <summary>Role keys of the context (<c>Roles.RoleKey</c> with <c>ContextKey = account</c>).</summary>
    public static class RoleKeys
    {
        /// <summary>The owner of the org.</summary>
        public const string Owner = "org_owner";

        /// <summary>The org administrator: everything the owner does in the account, but the ownership.</summary>
        public const string Admin = "org_admin";

        /// <summary>The accountant: reads the org's invoices.</summary>
        public const string Accountant = "org_accountant";
    }

    /// <summary>Permissions of the context (<c>RolePermissions.PermissionKey</c>).</summary>
    public static class Permissions
    {
        /// <summary>Invite, change, deactivate and remove the org's people (AM-02).</summary>
        public const string MembersManage = "org.members.manage";

        /// <summary>
        /// Plan, billing profile, payment methods, Stripe Connect, domain, branding. What the <c>OrgBillingAdmin</c> policy
        /// evaluates: its name stays, so every endpoint under it keeps working (AM-00).
        /// </summary>
        public const string BillingManage = "org.billing.manage";

        /// <summary>Read the org's invoices and subscription, change nothing.</summary>
        public const string BillingRead = "org.billing.read";

        /// <summary>Org name, slug, contacts and languages.</summary>
        public const string SettingsManage = "org.settings.manage";

        /// <summary>The org's trusted suppliers. Also held by the property managers, in the rental contexts they work in.</summary>
        public const string SuppliersManage = "org.suppliers.manage";

        /// <summary>The org's activity log.</summary>
        public const string ActivityRead = "org.activity.read";

        /// <summary>What the owner and the administrator hold.</summary>
        public static IReadOnlyList<string> All { get; } =
        [
            MembersManage, BillingManage, BillingRead, SettingsManage, SuppliersManage, ActivityRead,
        ];
    }
}
