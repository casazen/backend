using Casazen.Core.Authorization;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Casazen.Infrastructure.Services;

/// <inheritdoc cref="IOrgOverviewService" />
/// <remarks>
/// <para><b>A fixed number of statements.</b> Every figure is a count or a grouped count in SQL: the properties are counted by
/// mode and by «to complete» in one grouped query, the Alloggiati credentials with one <c>NOT EXISTS</c>, the invitations with a
/// count and a minimum; nothing is read per property or per person, so the cost does not grow with the org. A test on PostgreSQL
/// counts the commands at three sizes.</para>
/// <para><b>«To complete» is no new rule.</b> A short-term property is to complete while its compliance status is not active
/// (<see cref="PropertyComplianceStatus"/>), the same field the compliance cockpit and the onboarding checklist read; a property
/// in long-term mode has no listing to activate and is never to complete (PM-01).</para>
/// <para><b>The properties are the caller's own.</b> They are counted inside the <see cref="HostScope"/> it was given, so an
/// account whose reach is narrower than the org sees its own figures and not the whole org's.</para>
/// </remarks>
public sealed class OrgOverviewService(
    AppDbContext db,
    IEntitlementService entitlement,
    IOrgSeatService seats,
    IOrgActivityService activity,
    TimeProvider? timeProvider = null) : IOrgOverviewService
{
    /// <summary>The lines of people the home shows.</summary>
    internal const int RecentActivityCount = 3;

    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;

    public async Task<OrgOverview> GetAsync(
        Guid orgId,
        HostScope scope,
        IReadOnlySet<string> accountPermissions,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(accountPermissions);

        var now = _clock.GetUtcNow().UtcDateTime;

        string? standing = null;
        bool? openAccess = null;
        if (accountPermissions.Contains(OrgOverviewSections.Billing))
        {
            standing = await StandingAsync(orgId, cancellationToken);
            openAccess = (await entitlement.GetEntitlementAsync(orgId, cancellationToken)).OpenAccess;
        }

        OrgOverviewSeats? seatsSection = null;
        OrgOverviewInvitations? invitations = null;
        OrgOverviewAccessRequests? accessRequests = null;
        OrgOverviewProperties? properties = null;
        OrgOverviewAlloggiati? alloggiati = null;
        if (accountPermissions.Contains(OrgOverviewSections.People))
        {
            var usage = await seats.GetUsageAsync(orgId, cancellationToken);
            seatsSection = new OrgOverviewSeats(usage.Used, usage.Max, usage.IsUnlimited);
            invitations = await InvitationsAsync(orgId, now, cancellationToken);

            // The queue of the requests for access arrives with AM-02d: nobody is waiting before that.
            accessRequests = new OrgOverviewAccessRequests(0);
            properties = await PropertiesAsync(orgId, scope, cancellationToken);
            alloggiati = await AlloggiatiAsync(orgId, scope, cancellationToken);
        }

        // The trusted suppliers arrive with AM-10: the field exists from the start, that task fills it.
        var suppliers = accountPermissions.Contains(OrgOverviewSections.Suppliers) ? new OrgOverviewSuppliers(0) : null;

        IReadOnlyList<OrgActivityItem>? recent = null;
        if (accountPermissions.Contains(OrgOverviewSections.Activity))
        {
            var page = await activity.ListAsync(
                orgId, new OrgActivityFilter(PeopleOnly: true), page: 1, pageSize: RecentActivityCount, cancellationToken);
            recent = page.Items;
        }

        return new OrgOverview(standing, openAccess, seatsSection, properties, invitations, accessRequests, suppliers, alloggiati, recent);
    }

    /// <summary>
    /// «Attention» while a payment of the plan failed (<c>PastDue</c>), the retries ran out (<c>Unpaid</c>) or the first payment has
    /// not gone through (<c>Incomplete</c>); «ok» otherwise, an org that never subscribed (the open access) included.
    /// </summary>
    internal async Task<string> StandingAsync(Guid orgId, CancellationToken cancellationToken)
    {
        var status = await db.Orgs.AsNoTracking()
            .Where(o => o.Id == orgId)
            .Select(o => (SubscriptionStatus?)o.SubscriptionStatus)
            .FirstOrDefaultAsync(cancellationToken);

        return status is SubscriptionStatus.PastDue or SubscriptionStatus.Unpaid or SubscriptionStatus.Incomplete
            ? OrgOverviewStanding.Attention
            : OrgOverviewStanding.Ok;
    }

    internal async Task<OrgOverviewInvitations> InvitationsAsync(Guid orgId, DateTime now, CancellationToken cancellationToken)
    {
        // The same rule as the seats: pending and not past its expiry (an expired one the job has not marked yet is already out).
        var open = db.OrgInvitations.IgnoreQueryFilters().AsNoTracking()
            .Where(i => i.OrgId == orgId && i.Status == OrgInvitationStatus.Pending && i.ExpiresAt > now);

        var pending = await open.CountAsync(cancellationToken);
        if (pending == 0)
            return new OrgOverviewInvitations(0, null);

        var oldest = await open.MinAsync(i => (DateTime?)i.CreatedAt, cancellationToken);
        return new OrgOverviewInvitations(pending, oldest is { } since ? Math.Max(0, (int)Math.Floor((now - since).TotalDays)) : null);
    }

    internal async Task<OrgOverviewProperties> PropertiesAsync(Guid orgId, HostScope scope, CancellationToken cancellationToken)
    {
        var groups = await db.Properties.AsNoTracking()
            .Where(p => p.OrgId == orgId)
            .InScope(scope)
            .GroupBy(p => new
            {
                p.RentalMode,
                ToComplete = p.RentalMode == RentalMode.Short && p.IsActive && p.ComplianceStatus != PropertyComplianceStatus.Active,
            })
            .Select(g => new { g.Key.RentalMode, g.Key.ToComplete, Count = g.Count() })
            .ToListAsync(cancellationToken);

        var shortCount = groups.Where(g => g.RentalMode == RentalMode.Short).Sum(g => g.Count);
        var longCount = groups.Where(g => g.RentalMode == RentalMode.Long).Sum(g => g.Count);
        var toComplete = groups.Where(g => g.ToComplete).Sum(g => g.Count);
        return new OrgOverviewProperties(shortCount + longCount, shortCount, longCount, toComplete);
    }

    internal async Task<OrgOverviewAlloggiati> AlloggiatiAsync(Guid orgId, HostScope scope, CancellationToken cancellationToken)
    {
        // Alloggiati Web is a duty of the short-term stays: a long-term property needs no credentials.
        var without = await db.Properties.AsNoTracking()
            .Where(p => p.OrgId == orgId && p.RentalMode == RentalMode.Short)
            .InScope(scope)
            .CountAsync(p => !db.PropertyQuesturaCredentials.Any(c => c.PropertyId == p.Id), cancellationToken);

        return new OrgOverviewAlloggiati(without);
    }
}
