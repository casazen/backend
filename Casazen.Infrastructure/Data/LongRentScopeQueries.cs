using Casazen.Core.Authorization;
using Casazen.Core.Entities;

namespace Casazen.Infrastructure.Data;

/// <summary>
/// Where the long-term aggregates (lease list, rent register, agenda, overview: LR-01) start from: the rows of the org of
/// <see cref="HostScope.OrgId"/>, narrowed by <c>InScope</c> (AM-03) to the properties the caller reaches. Done in SQL, never by
/// filtering a loaded list. The org predicate is repeated on purpose: it is the tenant filter again, explicit, so a context
/// without a tenant (a job, a test) cannot widen it.
/// </summary>
/// <remarks>
/// <para>Nothing here decides who reaches what: that is <see cref="HostScopeQueryExtensions.InScope{T}"/>, the same rule as every
/// other list of the host (an org-wide scope sees the org; the collaborator «Solo alcuni» the properties it was given, by an
/// <c>EXISTS</c> on its grants; an account in no team the properties it created). The only thing added is the path from a rent
/// installment to its property, which <c>HostScopeQueryExtensions</c> has no overload for: lease, then property.</para>
/// </remarks>
internal static class LongRentScopeQueries
{
    public static IQueryable<LeaseContract> WithinScope(this IQueryable<LeaseContract> leases, HostScope scope)
    {
        ArgumentNullException.ThrowIfNull(scope);
        var orgId = scope.OrgId;
        return leases.Where(l => l.OrgId == orgId).InScope(scope);
    }

    public static IQueryable<RentLedgerEntry> WithinScope(this IQueryable<RentLedgerEntry> entries, HostScope scope)
    {
        ArgumentNullException.ThrowIfNull(scope);
        var orgId = scope.OrgId;
        return entries.Where(e => e.OrgId == orgId).InScope(scope, e => e.LeaseContract.Property);
    }
}
