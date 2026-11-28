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
    public const string Key = "account";

    public const string DisplayName = "Amministrazione";

    public const string DefaultRoute = "/app/account";

    public static bool IsAccountContext(string contextKey) =>
        string.Equals(contextKey, Key, StringComparison.OrdinalIgnoreCase);

    public static class RoleKeys
    {
        public const string Owner = "org_owner";

        public const string Admin = "org_admin";

        public const string Accountant = "org_accountant";

        /// <summary>
        /// The property manager (AM-02c, decision AM-D12): a restricted membership of the account context with the trusted
        /// suppliers only (<see cref="Permissions.SuppliersManage"/>). It never holds <see cref="Permissions.MembersManage"/>
        /// nor <see cref="Permissions.BillingManage"/>, so it reaches «Fornitori» and nothing else of the account area.
        /// </summary>
        public const string Manager = "org_manager";
    }

    public static class Permissions
    {
        public const string MembersManage = "org.members.manage";

        public const string BillingManage = "org.billing.manage";

        public const string BillingRead = "org.billing.read";

        public const string SettingsManage = "org.settings.manage";

        /// <summary>
        /// The org's trusted suppliers. Also held by the property managers, in the rental contexts they work in and, since
        /// AM-02c, by their <see cref="RoleKeys.Manager"/> membership of the account context.
        /// </summary>
        public const string SuppliersManage = "org.suppliers.manage";

        public const string ActivityRead = "org.activity.read";

        public static IReadOnlyList<string> All { get; } =
        [
            MembersManage, BillingManage, BillingRead, SettingsManage, SuppliersManage, ActivityRead,
        ];
    }
}
