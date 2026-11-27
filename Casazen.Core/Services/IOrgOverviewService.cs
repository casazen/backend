using Casazen.Core.Authorization;

namespace Casazen.Core.Services;

public static class OrgOverviewStanding
{
    public const string Ok = "ok";

    public const string Attention = "attention";
}

public sealed record OrgOverviewSeats(int Used, int Max, bool Unlimited);

public sealed record OrgOverviewProperties(int Total, int Short, int Long, int ToComplete);

public sealed record OrgOverviewInvitations(int Pending, int? OldestPendingDays);

public sealed record OrgOverviewAccessRequests(int Pending);

public sealed record OrgOverviewSuppliers(int Trusted);

/// <summary>Short-term properties of the caller that have no Alloggiati Web credentials.</summary>
public sealed record OrgOverviewAlloggiati(int PropertiesWithoutCredentials);

public sealed record OrgOverview(
    string? Standing,
    bool? OpenAccess,
    OrgOverviewSeats? Seats,
    OrgOverviewProperties? Properties,
    OrgOverviewInvitations? Invitations,
    OrgOverviewAccessRequests? AccessRequests,
    OrgOverviewSuppliers? Suppliers,
    OrgOverviewAlloggiati? Alloggiati,
    IReadOnlyList<OrgActivityItem>? RecentActivity);

public static class OrgOverviewSections
{
    public const string Billing = AccountContext.Permissions.BillingRead;

    /// <summary><c>seats</c>, <c>invitations</c>, <c>accessRequests</c>, <c>properties</c> and <c>alloggiati</c>: the people and the properties of the account.</summary>
    public const string People = AccountContext.Permissions.MembersManage;

    public const string Suppliers = AccountContext.Permissions.SuppliersManage;

    public const string Activity = AccountContext.Permissions.ActivityRead;

    public static IReadOnlyList<string> All { get; } = [Billing, People, Suppliers, Activity];
}

/// <summary>
/// The overview of the org for the home of the account (AM-02c, <c>GET /api/orgs/me/overview</c>): the subscription, the seats,
/// the properties, the invitations, the requests for access, the suppliers, the Alloggiati credentials and the latest activity of
/// people. Counts are done in SQL, a fixed number of statements whatever the size of the org (no query per property or per
/// person); the properties are the caller's own (<see cref="HostScope"/>).
/// </summary>
public interface IOrgOverviewService
{
    /// <param name="orgId">The caller's org (read from its account, never a value of the request).</param>
    /// <param name="scope">What the caller reaches of the host data: the properties are counted inside it.</param>
    /// <param name="accountPermissions">The permissions the caller holds in the account context, among <see cref="OrgOverviewSections.All"/>: a section whose permission is missing is left out.</param>
    Task<OrgOverview> GetAsync(
        Guid orgId,
        HostScope scope,
        IReadOnlySet<string> accountPermissions,
        CancellationToken cancellationToken = default);
}
