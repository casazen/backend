using Casazen.Core.Authorization;
using Casazen.Core.Entities;

namespace Casazen.Infrastructure.Data;

/// <summary>
/// The one place where the long-term aggregates (lease list, rent register, agenda, overview: LR-01) apply the caller's reach
/// to a query (TN-3): the rows of the org of <see cref="HostScope.OrgId"/> and, when the scope is not org-wide, only those of the
/// properties its <see cref="HostScope.OwnerId"/> owns. Done in SQL, never by filtering a loaded list. The org predicate is
/// repeated on purpose: it is the tenant filter again, explicit, so a context without a tenant (a job, a test) cannot widen it.
/// </summary>
/// <remarks>
/// <para>The same rule as the existing lease endpoints (<c>LeaseContractRepository.GetSummariesAsync</c>): an org-wide scope sees the
/// org, a scope with an owner sees the properties of that owner. <b>Any other restriction of a scope sees nothing</b> (fail closed):
/// the day <see cref="HostScope"/> gains a restriction these methods do not know (the per-property grants of AM-03,
/// <c>GrantedToUserId</c>), a person with it gets empty lists here rather than the whole org, until these two methods apply it
/// (<c>InScope</c> of AM-03 is the one line each needs).</para>
/// </remarks>
internal static class LongRentScopeQueries
{
    public static IQueryable<LeaseContract> WithinScope(this IQueryable<LeaseContract> leases, HostScope scope)
    {
        ArgumentNullException.ThrowIfNull(scope);
        var orgId = scope.OrgId;
        leases = leases.Where(l => l.OrgId == orgId);
        if (scope.IsOrgWide)
            return leases;

        if (scope.OwnerId is { } ownerId)
            return leases.Where(l => l.Property.OwnerId == ownerId);

        return leases.Where(l => false);
    }

    public static IQueryable<RentLedgerEntry> WithinScope(this IQueryable<RentLedgerEntry> entries, HostScope scope)
    {
        ArgumentNullException.ThrowIfNull(scope);
        var orgId = scope.OrgId;
        entries = entries.Where(e => e.OrgId == orgId);
        if (scope.IsOrgWide)
            return entries;

        if (scope.OwnerId is { } ownerId)
            return entries.Where(e => e.LeaseContract.Property.OwnerId == ownerId);

        return entries.Where(e => false);
    }
}
