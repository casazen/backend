using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Casazen.Infrastructure.Services;

/// <summary>
/// What the caller of the owner portal reaches now (E07/F07/US01, ADR-011-R2, AC-NF111-4). See <see cref="IOwnerPortalAccessResolver"/>.
/// </summary>
/// <remarks>
/// The caller of the portal has no org: the tenant filter of its request matches nothing, so every read here ignores it and names the org, the owner and
/// the property explicitly (as the acceptance of an owner invitation does). The soft-delete filter of the properties stays: a deleted property is never
/// reached. Nothing is cached: an ownership that ended, an owner removed or anonymized by the agency closes the access at the next request.
/// </remarks>
public sealed class OwnerPortalAccessResolver(AppDbContext db, TimeProvider timeProvider) : IOwnerPortalAccessResolver
{
    public async Task<OwnerPortalAccess> ResolveAsync(Guid ownerUserId, CancellationToken cancellationToken = default)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;

        var own = await InForce(db, now)
            .Where(o => o.OwnerUserId == ownerUserId)
            .Select(o => new { o.OrgId, o.PropertyOwnerId, o.PropertyId, o.OwnershipStart, o.OwnershipEnd })
            .ToListAsync(cancellationToken);

        var grants = own
            .Select(o => new OwnerPortalGrant(o.OrgId, o.PropertyOwnerId, o.PropertyId, o.OwnershipStart, o.OwnershipEnd, OwnerPortalPermissions.Everything, null))
            .ToList();

        // E07/F07/US01/T02: the delegations the owners gave this account, active now, with the permissions given, on the ownerships in force of the owner
        // that gave them. A withdrawn contact, an owner whose ownership ended or was removed: nothing, from this request on.
        var delegations = await db.OwnerPortalContacts
            .IgnoreQueryFilters([AppDbContext.TenantQueryFilter])
            .AsNoTracking()
            .Where(c => c.OwnerUserId == ownerUserId && c.Status == OwnerPortalContactStatus.Active)
            .Select(c => new { c.Id, c.OrgId, c.PropertyOwnerId, c.Permissions })
            .ToListAsync(cancellationToken);
        if (delegations.Count > 0)
        {
            var ownerIds = delegations.Select(d => d.PropertyOwnerId).Distinct().ToList();
            var ofTheOwners = await InForce(db, now)
                .Where(o => ownerIds.Contains(o.PropertyOwnerId))
                .Select(o => new { o.OrgId, o.PropertyOwnerId, o.PropertyId, o.OwnershipStart, o.OwnershipEnd })
                .ToListAsync(cancellationToken);
            foreach (var delegation in delegations.Where(d => d.Permissions != OwnerPortalPermission.None))
            {
                grants.AddRange(ofTheOwners
                    .Where(o => o.OrgId == delegation.OrgId && o.PropertyOwnerId == delegation.PropertyOwnerId)
                    .Select(o => new OwnerPortalGrant(o.OrgId, o.PropertyOwnerId, o.PropertyId, o.OwnershipStart, o.OwnershipEnd, delegation.Permissions, delegation.Id)));
            }
        }

        return new OwnerPortalAccess(ownerUserId, grants);
    }

    /// <summary>
    /// The ownerships in force at <paramref name="now"/> (started, not ended, ADR-011-R8) on a property of the same org that is not deleted, of an owner profile
    /// of that org that is not anonymized, across every org (the tenant filter of the caller is ignored).
    /// </summary>
    internal static IQueryable<PropertyOwnership> InForce(AppDbContext db, DateTime now)
    {
        // The subqueries are built outside the predicates: an expression tree cannot hold the list of the filters to ignore.
        var properties = db.Properties.IgnoreQueryFilters([AppDbContext.TenantQueryFilter]);
        var owners = db.PropertyOwners.IgnoreQueryFilters([AppDbContext.TenantQueryFilter]);
        return db.PropertyOwnerships
            .IgnoreQueryFilters([AppDbContext.TenantQueryFilter])
            .AsNoTracking()
            .Where(o => o.OwnershipStart <= now && (o.OwnershipEnd == null || o.OwnershipEnd >= now))
            .Where(o => properties.Any(p => p.Id == o.PropertyId && p.OrgId == o.OrgId))
            .Where(o => owners.Any(owner => owner.Id == o.PropertyOwnerId && owner.OrgId == o.OrgId && owner.AnonymizedAt == null));
    }
}
