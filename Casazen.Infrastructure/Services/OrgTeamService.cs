using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.OrgTeam;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Services;

/// <inheritdoc cref="IOrgTeamService" />
/// <remarks>
/// <para>Every write takes the org's people lock (<see cref="PostgresAdvisoryLocks.Scope.OrgMembership"/>) <b>before</b> it
/// reads the member and checks who may do what, so the rules are decided on the roles as they are at that moment, not as
/// they were when the page was drawn (an administrator cannot act on someone who has just become one). The writes
/// themselves are <see cref="IOrgMembershipService"/>'s, which joins the open transaction. The reactivation also takes the
/// seats lock first, like every other decision about seats.</para>
/// <para>The cache of the person is invalidated once more after the commit: the inner writes did it before it.</para>
/// </remarks>
public sealed class OrgTeamService(
    AppDbContext db,
    IOrgSeatService seats,
    IOrgMembershipService membership,
    IUserAuthorizationCache authorizationCache,
    ILogger<OrgTeamService> logger) : IOrgTeamService
{
    public async Task<OrgTeamView> ListAsync(Guid orgId, CancellationToken cancellationToken = default)
    {
        var members = await ReadMembersAsync(orgId, memberId: null, cancellationToken);
        var usage = await seats.GetUsageAsync(orgId, cancellationToken);
        return new OrgTeamView(members, usage);
    }

    public async Task<OrgMemberView> ChangeRoleAsync(
        Guid orgId,
        Guid memberId,
        OrgRole role,
        string actorUserId,
        CancellationToken cancellationToken = default)
    {
        if (role == OrgRole.Owner)
            throw new DomainRuleException(OrgMembershipErrors.OwnerNotAssignable, "OrgMemberOwnerNotAssignable");

        OrgMember target;
        await using (var transaction = await PostgresAdvisoryLocks.BeginLockedTransactionAsync(
                         db, cancellationToken, OrgMembershipService.OrgLock(orgId)))
        {
            OrgMember actor;
            (actor, target) = await LoadAsync(orgId, memberId, actorUserId, cancellationToken);
            EnsureCanAct(actor, target);
            if (!OrgTeamRules.CanAssign(actor.Role, role))
                throw new DomainForbiddenException(OrgInvitationErrors.OwnerRequired, "OrgOwnerRequired");

            await membership.ChangeRoleAsync(target.UserId, role, cancellationToken);

            if (transaction is not null)
                await transaction.CommitAsync(cancellationToken);
        }

        authorizationCache.Invalidate(target.UserId);
        logger.LogInformation(
            "Org member role changed by the team service: memberId={MemberId} orgId={OrgId} from={From} to={To} by={ActorUserId}",
            memberId, orgId, target.Role, role, actorUserId);

        return await ViewAsync(orgId, memberId, cancellationToken);
    }

    public async Task<OrgMemberView> DeactivateAsync(
        Guid orgId,
        Guid memberId,
        string actorUserId,
        CancellationToken cancellationToken = default)
    {
        OrgMember target;
        await using (var transaction = await PostgresAdvisoryLocks.BeginLockedTransactionAsync(
                         db, cancellationToken, OrgMembershipService.OrgLock(orgId)))
        {
            OrgMember actor;
            (actor, target) = await LoadAsync(orgId, memberId, actorUserId, cancellationToken);
            EnsureCanAct(actor, target);

            await membership.DeactivateAsync(target.UserId, cancellationToken);

            if (transaction is not null)
                await transaction.CommitAsync(cancellationToken);
        }

        authorizationCache.Invalidate(target.UserId);
        logger.LogInformation(
            "Org member deactivated by the team service: memberId={MemberId} orgId={OrgId} by={ActorUserId}",
            memberId, orgId, actorUserId);

        return await ViewAsync(orgId, memberId, cancellationToken);
    }

    public async Task<OrgMemberView> ReactivateAsync(
        Guid orgId,
        Guid memberId,
        string actorUserId,
        CancellationToken cancellationToken = default)
    {
        OrgMember target;
        await using (var transaction = await OrgTeamAccess.BeginSeatsTransactionAsync(
                         db, orgId, cancellationToken, OrgMembershipService.OrgLock(orgId)))
        {
            OrgMember actor;
            (actor, target) = await LoadAsync(orgId, memberId, actorUserId, cancellationToken);
            EnsureCanAct(actor, target);

            // A deactivated member holds no seat: coming back takes one, if the plan has it.
            if (target.Status == OrgMemberStatus.Deactivated)
                await seats.EnsureSeatAvailableAsync(orgId, cancellationToken);

            await membership.ReactivateAsync(target.UserId, cancellationToken);

            if (transaction is not null)
                await transaction.CommitAsync(cancellationToken);
        }

        authorizationCache.Invalidate(target.UserId);
        logger.LogInformation(
            "Org member reactivated by the team service: memberId={MemberId} orgId={OrgId} by={ActorUserId}",
            memberId, orgId, actorUserId);

        return await ViewAsync(orgId, memberId, cancellationToken);
    }

    public async Task RemoveAsync(
        Guid orgId,
        Guid memberId,
        string actorUserId,
        CancellationToken cancellationToken = default)
    {
        OrgMember target;
        await using (var transaction = await PostgresAdvisoryLocks.BeginLockedTransactionAsync(
                         db, cancellationToken, OrgMembershipService.OrgLock(orgId)))
        {
            OrgMember actor;
            (actor, target) = await LoadAsync(orgId, memberId, actorUserId, cancellationToken);
            EnsureCanAct(actor, target);

            await membership.RemoveAsync(target.UserId, cancellationToken);

            if (transaction is not null)
                await transaction.CommitAsync(cancellationToken);
        }

        authorizationCache.Invalidate(target.UserId);
        logger.LogInformation(
            "Org member removed by the team service: memberId={MemberId} orgId={OrgId} by={ActorUserId}",
            memberId, orgId, actorUserId);
    }

    // ─── Rules and reads ────────────────────────────────────────────────────────────────────────────────

    /// <summary>The caller and the member it acts on, both from their rows (read under the lock), or 404 for a member of another org.</summary>
    private async Task<(OrgMember Actor, OrgMember Target)> LoadAsync(
        Guid orgId,
        Guid memberId,
        string actorUserId,
        CancellationToken cancellationToken)
    {
        var actor = await OrgTeamAccess.RequireManagerAsync(db, orgId, actorUserId, cancellationToken);

        // AsNoTracking: the membership service reads the member again, tracked, under the same lock.
        var target = await db.OrgMembers.IgnoreQueryFilters().AsNoTracking()
            .FirstOrDefaultAsync(m => m.Id == memberId && m.OrgId == orgId, cancellationToken)
            ?? throw new NotFoundException($"Org member {memberId} not found")
            {
                Code = OrgInvitationErrors.MemberNotFound,
                MessageKey = "OrgMemberNotFound",
            };

        return (actor, target);
    }

    /// <summary>The owner is out of reach for everyone; an administrator is in the hands of the owner only.</summary>
    private static void EnsureCanAct(OrgMember actor, OrgMember target)
    {
        if (target.Role == OrgRole.Owner)
            throw new DomainConflictException(OrgMembershipErrors.LastOwner, "OrgLastOwner");

        if (!OrgTeamRules.CanActOn(actor.Role, target.Role))
            throw new DomainForbiddenException(OrgInvitationErrors.OwnerRequired, "OrgOwnerRequired");
    }

    private async Task<OrgMemberView> ViewAsync(Guid orgId, Guid memberId, CancellationToken cancellationToken) =>
        (await ReadMembersAsync(orgId, memberId, cancellationToken)).Single();

    /// <summary>The org's members (or only <paramref name="memberId"/>), owner first, then by name, with the areas each works in.</summary>
    private async Task<IReadOnlyList<OrgMemberView>> ReadMembersAsync(
        Guid orgId,
        Guid? memberId,
        CancellationToken cancellationToken)
    {
        var members = db.OrgMembers.IgnoreQueryFilters().AsNoTracking().Where(m => m.OrgId == orgId);
        if (memberId is { } only)
            members = members.Where(m => m.Id == only);

        var rows = await (
                from m in members
                join u in db.Users.AsNoTracking() on m.UserId equals u.Id
                select new
                {
                    m.Id,
                    m.UserId,
                    u.Email,
                    u.FirstName,
                    u.LastName,
                    m.Role,
                    m.Status,
                    m.PropertyScope,
                    m.CreatedAt,
                    m.DeactivatedAt,
                })
            .ToListAsync(cancellationToken);

        var userIds = rows.Select(r => r.UserId).ToList();
        var areas = await db.UserContextMemberships.AsNoTracking()
            .Where(c => userIds.Contains(c.UserId)
                        && (c.ContextKey == OrgRoleCatalog.ShortRent || c.ContextKey == OrgRoleCatalog.LongRent))
            .Select(c => new { c.UserId, c.ContextKey })
            .ToListAsync(cancellationToken);
        var areasByUser = areas
            .GroupBy(a => a.UserId)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<string>)g.Select(a => a.ContextKey).Order(StringComparer.Ordinal).ToList());

        return rows
            .OrderBy(r => r.Role == OrgRole.Owner ? 0 : 1)
            .ThenBy(r => r.LastName, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(r => r.FirstName, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(r => r.Id)
            .Select(r => new OrgMemberView(
                r.Id,
                r.UserId,
                r.Email,
                r.FirstName,
                r.LastName,
                r.Role,
                r.Status,
                r.PropertyScope,
                areasByUser.TryGetValue(r.UserId, out var memberAreas) ? memberAreas : [],
                r.CreatedAt,
                r.DeactivatedAt))
            .ToList();
    }
}
