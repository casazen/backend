using Casazen.Core.Authorization;

namespace Casazen.Core.Services;

/// <summary>How the subscription of the org stands (AM-02c): the two words the home of the account shows.</summary>
public static class OrgOverviewStanding
{
    /// <summary>Nothing to do about the payment of the plan.</summary>
    public const string Ok = "ok";

    /// <summary>A payment of the plan failed (<c>PastDue</c>), or the subscription is <c>Unpaid</c> or <c>Incomplete</c>.</summary>
    public const string Attention = "attention";
}

/// <summary>Seats in use out of what the plan allows (the same numbers as <c>GET /api/orgs/me/members</c>); <paramref name="Max"/> is int max when unlimited.</summary>
public sealed record OrgOverviewSeats(int Used, int Max, bool Unlimited);

/// <summary>
/// The properties the caller reaches: how many, how many of each mode, and how many short-term ones are not yet able to receive
/// bookings (<c>ToComplete</c>: the compliance status is not active, the field that already says it; no new rule).
/// </summary>
public sealed record OrgOverviewProperties(int Total, int Short, int Long, int ToComplete);

/// <summary>Invitations that can still be accepted, and how many whole days the oldest of them has been waiting (<c>null</c> when there is none).</summary>
public sealed record OrgOverviewInvitations(int Pending, int? OldestPendingDays);

/// <summary>Requests for access waiting for an answer. <c>0</c> until the queue of the requests exists (AM-02d).</summary>
public sealed record OrgOverviewAccessRequests(int Pending);

/// <summary>Trusted suppliers of the org. <c>0</c> until the trusted suppliers exist (AM-10): the field is there from the start, that task fills it.</summary>
public sealed record OrgOverviewSuppliers(int Trusted);

/// <summary>Short-term properties of the caller that have no Alloggiati Web credentials.</summary>
public sealed record OrgOverviewAlloggiati(int PropertiesWithoutCredentials);

/// <summary>
/// The one read the home of the account (AM-04) needs, so the page does not compose six requests and repeat the permissions of
/// each. A section is <c>null</c> (left out of the answer) when the caller lacks the permission that section belongs to.
/// </summary>
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

/// <summary>Which permission of the account context each section of <see cref="OrgOverview"/> needs.</summary>
public static class OrgOverviewSections
{
    /// <summary><c>standing</c> and <c>openAccess</c>: the subscription is the billing's.</summary>
    public const string Billing = AccountContext.Permissions.BillingRead;

    /// <summary><c>seats</c>, <c>invitations</c>, <c>accessRequests</c>, <c>properties</c> and <c>alloggiati</c>: the people and the properties of the account.</summary>
    public const string People = AccountContext.Permissions.MembersManage;

    /// <summary><c>suppliers</c>.</summary>
    public const string Suppliers = AccountContext.Permissions.SuppliersManage;

    /// <summary><c>recentActivity</c>: the activity log.</summary>
    public const string Activity = AccountContext.Permissions.ActivityRead;

    /// <summary>The permissions the controller asks the caller about.</summary>
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
