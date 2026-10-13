using Casazen.Core.Entities.Enums;
using Microsoft.EntityFrameworkCore;

namespace Casazen.Infrastructure.Data;

/// <summary>
/// What the request tenant reads about the caller, in one row: the org that scopes its queries, whether its account is
/// active and the status of its org membership (<c>TenantContext</c>).
/// </summary>
/// <param name="OrgId">The caller's <b>host</b> org; <c>null</c> for a user without one (PL-05).</param>
/// <param name="IsActive"><c>Users.IsActive</c> (PL-03).</param>
/// <param name="MemberStatus"><c>OrgMembers.Status</c> of the caller; <c>null</c> when it is in no org team (AM-01).</param>
public sealed record CallerTenantRow(Guid? OrgId, bool IsActive, OrgMemberStatus? MemberStatus);

/// <summary>
/// The one query behind the request tenant (<c>TenantContext.ResolveAsync</c>): a single read per request of the caller's
/// org, active flag and org membership status, without any cache, so a deactivation applies from the next request. A
/// type of its own so that a test can read the SQL the Npgsql provider generates for it.
/// </summary>
public static class CallerTenantQuery
{
    /// <summary>
    /// The caller's row. <c>A1-40</c>: <c>User.OrgId</c> can point at the caller's own supplier org (linked at supplier
    /// registration, before ever completing the host onboarding); only a <c>Host</c> org scopes the tenant filter, so a
    /// supplier account never reads or writes host data through it (the join resolves to no org, fail-closed).
    /// <b>AM-01:</b> <c>OrgMembers</c> is tenant-owned and the request has no tenant yet (this is what resolves it), so its
    /// filter would match nothing: <c>IgnoreQueryFilters</c>, scoped to the caller's own row (<c>m.UserId == u.Id</c>).
    /// </summary>
    public static IQueryable<CallerTenantRow> For(AppDbContext db, string userId) =>
        db.Users.AsNoTracking()
            .Where(u => u.Id == userId)
            .Select(u => new CallerTenantRow(
                u.OrgId != null && u.Org!.OrgType == OrgType.Host ? u.OrgId : null,
                u.IsActive,
                db.OrgMembers
                    .IgnoreQueryFilters()
                    .Where(m => m.UserId == u.Id)
                    .Select(m => (OrgMemberStatus?)m.Status)
                    .FirstOrDefault()));
}
