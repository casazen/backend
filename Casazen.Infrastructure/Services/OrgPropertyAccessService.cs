using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.OrgTeam;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Services;

/// <inheritdoc cref="IOrgPropertyAccessService" />
/// <remarks>
/// <para>Same discipline as the other writes of the org's people (<see cref="OrgTeamService"/>): the org's people lock first,
/// then the actor and the member read under it, so the rules are decided on the roles as they are at that moment (a role
/// change that makes the member a property manager cannot interleave with a grant), one <c>SaveChanges</c>, the cache of the
/// member invalidated once the transaction is committed.</para>
/// <para>The tables are read with <c>IgnoreQueryFilters</c> scoped to the org passed in, which is the caller's own (read
/// from its account by the controller), never a value of the request.</para>
/// </remarks>
public sealed class OrgPropertyAccessService(
    AppDbContext db,
    IUserAuthorizationCache authorizationCache,
    ILogger<OrgPropertyAccessService> logger,
    TimeProvider? timeProvider = null) : IOrgPropertyAccessService
{
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;

    public async Task<OrgMemberPropertyAccessView> GetAsync(
        Guid orgId,
        Guid memberId,
        CancellationToken cancellationToken = default)
    {
        var member = await db.OrgMembers.IgnoreQueryFilters().AsNoTracking()
            .FirstOrDefaultAsync(m => m.Id == memberId && m.OrgId == orgId, cancellationToken)
            ?? throw MemberNotFound(memberId);

        return await BuildViewAsync(orgId, member, cancellationToken);
    }

    public async Task<OrgMemberPropertyAccessView> SetAsync(
        Guid orgId,
        Guid memberId,
        string actorUserId,
        PropertyScope scope,
        IReadOnlyCollection<Guid> propertyIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(propertyIds);
        if (!Enum.IsDefined(scope))
            throw new ArgumentOutOfRangeException(nameof(scope), scope, "Unknown property scope.");

        OrgMember target;
        await using (var transaction = await PostgresAdvisoryLocks.BeginLockedTransactionAsync(
                         db, cancellationToken, OrgMembershipService.OrgLock(orgId)))
        {
            var actor = await OrgTeamAccess.RequireManagerAsync(db, orgId, actorUserId, cancellationToken);

            // Tracked: its scope is written below.
            target = await db.OrgMembers.IgnoreQueryFilters()
                .FirstOrDefaultAsync(m => m.Id == memberId && m.OrgId == orgId, cancellationToken)
                ?? throw MemberNotFound(memberId);

            // An administrator is in the hands of the owner only; the owner is not limited by anybody (it is not a collaborator,
            // so «Solo alcuni» is refused below, and «Tutti» changes nothing).
            if (target.Role != OrgRole.Owner && !OrgTeamRules.CanActOn(actor.Role, target.Role))
                throw new DomainForbiddenException(OrgInvitationErrors.OwnerRequired, "OrgOwnerRequired");

            // «Solo alcuni» is the collaborator's; everybody else reaches the whole org.
            if (scope == PropertyScope.Selected && target.Role != OrgRole.Collaborator)
                throw new DomainRuleException(OrgMembershipErrors.ScopeNotSupported, "OrgMemberScopeNotSupported");

            var wanted = scope == PropertyScope.Selected ? propertyIds.ToHashSet() : [];
            await EnsurePropertiesExistAsync(orgId, wanted, cancellationToken);

            var grants = await db.PropertyMemberAccesses.IgnoreQueryFilters()
                .Where(a => a.OrgId == orgId && a.UserId == target.UserId)
                .ToListAsync(cancellationToken);

            db.PropertyMemberAccesses.RemoveRange(grants.Where(g => !wanted.Contains(g.PropertyId)));
            var now = _clock.GetUtcNow().UtcDateTime;
            foreach (var propertyId in wanted.Where(id => grants.All(g => g.PropertyId != id)))
            {
                db.PropertyMemberAccesses.Add(new PropertyMemberAccess
                {
                    OrgId = orgId,
                    UserId = target.UserId,
                    PropertyId = propertyId,
                    CreatedAt = now,
                    CreatedByUserId = actor.UserId,
                });
            }

            target.PropertyScope = target.Role == OrgRole.Collaborator ? scope : PropertyScope.All;

            await db.SaveChangesAsync(cancellationToken);
            if (transaction is not null)
                await transaction.CommitAsync(cancellationToken);
        }

        // The member's own snapshot carries its scope and its grants: this instance sees the change on its next request, the
        // others when their cached copy expires (Authorization:UserCacheSeconds, 60 s). Narrowing the access is therefore
        // seen by the other instances within a minute at most (docs/runbooks/org-team.md, section 26).
        authorizationCache.Invalidate(target.UserId);
        logger.LogInformation(
            "Org member property access set: memberId={MemberId} orgId={OrgId} scope={Scope} properties={Count} by={ActorUserId}",
            memberId, orgId, target.PropertyScope, propertyIds.Count, actorUserId);

        return await BuildViewAsync(orgId, target, cancellationToken);
    }

    public async Task SetResponsibleAsync(
        Guid orgId,
        Guid propertyId,
        string? responsibleUserId,
        CancellationToken cancellationToken = default)
    {
        var property = await db.Properties
            .FirstOrDefaultAsync(p => p.Id == propertyId && p.OrgId == orgId, cancellationToken)
            ?? throw new NotFoundException($"Property {propertyId} not found")
            {
                Code = "property_not_found",
                MessageKey = "PropertyNotFound",
            };

        if (!string.IsNullOrWhiteSpace(responsibleUserId))
            await EnsureCanBeInChargeAsync(orgId, propertyId, responsibleUserId, cancellationToken);

        property.ResponsibleUserId = string.IsNullOrWhiteSpace(responsibleUserId) ? null : responsibleUserId;
        property.UpdatedAt = _clock.GetUtcNow().UtcDateTime;
        await db.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "Property responsible set: propertyId={PropertyId} orgId={OrgId} responsible={Named}",
            propertyId, orgId, property.ResponsibleUserId is not null);
    }

    // ─── Rules and reads ────────────────────────────────────────────────────────────────────────────────

    /// <summary>422 <see cref="OrgMembershipErrors.PropertyUnknown"/> when an id is not a (not deleted) property of the org.</summary>
    private async Task EnsurePropertiesExistAsync(Guid orgId, IReadOnlySet<Guid> propertyIds, CancellationToken cancellationToken)
    {
        if (propertyIds.Count == 0)
            return;

        var ids = propertyIds.ToList();
        var known = await db.Properties.AsNoTracking()
            .CountAsync(p => p.OrgId == orgId && ids.Contains(p.Id), cancellationToken);
        if (known != ids.Count)
            throw new DomainRuleException(OrgMembershipErrors.PropertyUnknown, "OrgMemberPropertyUnknown");
    }

    /// <summary>The person in charge is an active member of the org, with an active account, who reaches the property.</summary>
    private async Task EnsureCanBeInChargeAsync(
        Guid orgId,
        Guid propertyId,
        string userId,
        CancellationToken cancellationToken)
    {
        var member = await db.OrgMembers.IgnoreQueryFilters().AsNoTracking()
            .Where(m => m.UserId == userId && m.OrgId == orgId && m.Status == OrgMemberStatus.Active)
            .Select(m => new { m.Role, m.PropertyScope })
            .FirstOrDefaultAsync(cancellationToken);
        var activeAccount = member is not null
                            && await db.Users.AsNoTracking()
                                .AnyAsync(u => u.Id == userId && u.OrgId == orgId && u.IsActive, cancellationToken);
        var reaches = activeAccount
                      && (member!.Role != OrgRole.Collaborator
                          || member.PropertyScope != PropertyScope.Selected
                          || await db.PropertyMemberAccesses.IgnoreQueryFilters().AsNoTracking()
                              .AnyAsync(a => a.UserId == userId && a.OrgId == orgId && a.PropertyId == propertyId, cancellationToken));

        if (!reaches)
            throw new DomainRuleException(PropertyResponsibleErrors.Invalid, "PropertyResponsibleInvalid");
    }

    private async Task<OrgMemberPropertyAccessView> BuildViewAsync(Guid orgId, OrgMember member, CancellationToken cancellationToken)
    {
        // Only a collaborator can be limited; for everybody else every property is granted whatever the stored scope says.
        var scopeSupported = member.Role == OrgRole.Collaborator;
        var restricted = scopeSupported && member.PropertyScope == PropertyScope.Selected;

        var properties = await db.Properties.AsNoTracking()
            .Where(p => p.OrgId == orgId && p.IsActive)
            .OrderBy(p => p.Name)
            .ThenBy(p => p.Id)
            .Select(p => new { p.Id, p.Name, p.City })
            .ToListAsync(cancellationToken);

        var grantedToMember = restricted
            ? (await db.PropertyMemberAccesses.IgnoreQueryFilters().AsNoTracking()
                    .Where(a => a.OrgId == orgId && a.UserId == member.UserId)
                    .Select(a => a.PropertyId)
                    .ToListAsync(cancellationToken))
                .ToHashSet()
            : null;

        // «Chi può accedere», two queries whatever the number of properties: the members who reach everything, and the
        // grants of the active collaborators «Solo alcuni» counted per property.
        var reachEverything = await db.OrgMembers.IgnoreQueryFilters().AsNoTracking()
            .CountAsync(
                m => m.OrgId == orgId
                     && m.Status == OrgMemberStatus.Active
                     && (m.Role != OrgRole.Collaborator || m.PropertyScope == PropertyScope.All),
                cancellationToken);
        var grantedCounts = (await (
                    from access in db.PropertyMemberAccesses.IgnoreQueryFilters().AsNoTracking()
                    join person in db.OrgMembers.IgnoreQueryFilters().AsNoTracking() on access.UserId equals person.UserId
                    where access.OrgId == orgId
                          && person.OrgId == orgId
                          && person.Status == OrgMemberStatus.Active
                          && person.Role == OrgRole.Collaborator
                          && person.PropertyScope == PropertyScope.Selected
                    group access by access.PropertyId into byProperty
                    select new { PropertyId = byProperty.Key, People = byProperty.Count() })
                .ToListAsync(cancellationToken))
            .ToDictionary(x => x.PropertyId, x => x.People);

        var items = properties
            .Select(p => new PropertyAccessItem(
                p.Id,
                p.Name,
                p.City,
                grantedToMember is null || grantedToMember.Contains(p.Id),
                reachEverything + grantedCounts.GetValueOrDefault(p.Id)))
            .ToList();

        return new OrgMemberPropertyAccessView(
            member.Id,
            member.Role,
            scopeSupported ? member.PropertyScope : PropertyScope.All,
            scopeSupported,
            items);
    }

    private static NotFoundException MemberNotFound(Guid memberId) => new($"Org member {memberId} not found")
    {
        Code = OrgInvitationErrors.MemberNotFound,
        MessageKey = "OrgMemberNotFound",
    };
}
