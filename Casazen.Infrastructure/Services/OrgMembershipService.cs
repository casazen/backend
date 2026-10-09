using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Casazen.Infrastructure.Services;

/// <inheritdoc cref="IOrgMembershipService" />
/// <remarks>
/// <para>Every write is one <c>SaveChanges</c> (one transaction on PostgreSQL) holding the <see cref="OrgMember"/> row and
/// the membership rows together, under the advisory lock of the org
/// (<see cref="PostgresAdvisoryLocks.Scope.OrgMembership"/>), followed by the cache invalidation of the user.</para>
/// <para><c>OrgMembers</c> is tenant-owned, but this service acts across orgs and before a request has a tenant (the
/// onboarding creates the org in the same request): every query on it is <c>IgnoreQueryFilters</c>, scoped by the user
/// or the org explicitly.</para>
/// </remarks>
public sealed partial class OrgMembershipService(
    AppDbContext db,
    IUserAuthorizationCache authorizationCache,
    ILogger<OrgMembershipService> logger,
    TimeProvider? timeProvider = null) : IOrgMembershipService
{
    /// <summary>The contexts whose memberships an org role decides: the account and the two rental contexts.</summary>
    private static readonly string[] ManagedContexts =
        [AccountContext.Key, OrgRoleCatalog.ShortRent, OrgRoleCatalog.LongRent];

    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;

    public async Task<OrgMember> EnsureOwnerAsync(string userId, Guid orgId, CancellationToken cancellationToken = default)
    {
        await using var transaction = await PostgresAdvisoryLocks.BeginLockedTransactionAsync(
            db, cancellationToken, OrgLock(orgId));

        var userOrgId = await db.Users.AsNoTracking()
            .Where(u => u.Id == userId)
            .Select(u => new { u.OrgId })
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw new NotFoundException($"User {userId} not found");
        if (userOrgId.OrgId != orgId)
            throw new DomainConflictException(OrgMembershipErrors.OtherOrg, "OrgMemberOtherOrg");

        var member = await db.OrgMembers.IgnoreQueryFilters()
            .FirstOrDefaultAsync(m => m.UserId == userId, cancellationToken);
        if (member is not null && (member.OrgId != orgId || member.Role != OrgRole.Owner))
            throw new DomainConflictException(OrgMembershipErrors.AlreadyMember, "OrgMemberAlreadyMember");

        var created = member is null;
        if (member is null)
        {
            // A removed member no longer has a row, and the org still has its owner (the owner cannot be removed).
            // Creating another Owner here is how that person would take the org over.
            if (await db.OrgMembers.IgnoreQueryFilters().AnyAsync(
                    m => m.OrgId == orgId && m.Role == OrgRole.Owner, cancellationToken))
            {
                throw new DomainConflictException(OrgMembershipErrors.AlreadyMember, "OrgMemberAlreadyMember");
            }

            member = NewMember(userId, orgId, OrgRole.Owner, createdByUserId: null);
            db.OrgMembers.Add(member);
        }

        // Only the account row: the owner's rental rows are the onboarding's (rental type), never touched here. The
        // projection must not stop a new customer from onboarding: a database without the seeded account role (the
        // migration is applied at every startup, so only a test database) still gets the org member row, the source of
        // truth, and the account row follows from the reconcile command.
        var changes = await ProjectAsync(
            userId,
            [new ProjectedRole(AccountContext.Key, AccountContext.RoleKeys.Owner)],
            [AccountContext.Key],
            cancellationToken,
            requireRoles: false);

        await SaveAsync(transaction, userId, cancellationToken);

        if (created || changes.Count > 0)
            logger.LogInformation("Org owner ensured: userId={UserId} orgId={OrgId} created={Created}", userId, orgId, created);
        return member;
    }

    public async Task<OrgMember> AddMemberAsync(
        string userId,
        Guid orgId,
        OrgRole role,
        IReadOnlyCollection<string> rentalContexts,
        string? createdByUserId,
        PropertyScope propertyScope = PropertyScope.All,
        CancellationToken cancellationToken = default)
    {
        if (role == OrgRole.Owner)
            throw new DomainRuleException(OrgMembershipErrors.OwnerNotAssignable, "OrgMemberOwnerNotAssignable");

        // «Solo alcuni» is the collaborator's (AM-03): everybody else reaches every property of the org.
        if (propertyScope == PropertyScope.Selected && role != OrgRole.Collaborator)
            throw new DomainRuleException(OrgMembershipErrors.ScopeNotSupported, "OrgMemberScopeNotSupported");

        var areas = (rentalContexts ?? [])
            .Where(OrgRoleCatalog.IsRentalContext)
            .Select(c => c.ToLowerInvariant())
            .Distinct()
            .ToList();
        if (areas.Count == 0)
            throw new DomainRuleException(OrgMembershipErrors.AreaRequired, "OrgMemberAreaRequired");

        await using var transaction = await PostgresAdvisoryLocks.BeginLockedTransactionAsync(
            db, cancellationToken, OrgLock(orgId));

        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == userId, cancellationToken)
            ?? throw new NotFoundException($"User {userId} not found");
        if (!await db.Orgs.AnyAsync(o => o.Id == orgId && o.OrgType == OrgType.Host, cancellationToken))
            throw new NotFoundException($"Org {orgId} not found");

        if (user.OrgId is Guid current && current != orgId)
            throw new DomainConflictException(OrgMembershipErrors.OtherOrg, "OrgMemberOtherOrg");
        if (await db.OrgMembers.IgnoreQueryFilters().AnyAsync(m => m.UserId == userId, cancellationToken))
            throw new DomainConflictException(OrgMembershipErrors.AlreadyMember, "OrgMemberAlreadyMember");

        if (user.OrgId is null)
        {
            user.OrgId = orgId;
            user.UpdatedAt = _clock.GetUtcNow().UtcDateTime;
        }

        var member = NewMember(userId, orgId, role, createdByUserId, propertyScope);
        db.OrgMembers.Add(member);
        await ProjectAsync(userId, OrgRoleCatalog.ProjectionOf(role, areas), ManagedContexts, cancellationToken);

        await SaveAsync(transaction, userId, cancellationToken);

        logger.LogInformation(
            "Org member added: userId={UserId} orgId={OrgId} role={Role} areas=[{Areas}] scope={Scope} by={CreatedBy}",
            userId, orgId, role, string.Join(", ", areas), propertyScope, createdByUserId);
        return member;
    }

    public async Task<OrgMember> ChangeRoleAsync(string userId, OrgRole role, CancellationToken cancellationToken = default)
    {
        if (role == OrgRole.Owner)
            throw new DomainRuleException(OrgMembershipErrors.OwnerNotAssignable, "OrgMemberOwnerNotAssignable");

        var (transaction, member) = await LockMemberAsync(userId, cancellationToken);
        await using var memberLock = transaction;

        if (member.Role == OrgRole.Owner)
            throw new DomainConflictException(OrgMembershipErrors.LastOwner, "OrgLastOwner");

        var previous = member.Role;
        member.Role = role;

        // Only the collaborator can be limited to some properties (AM-03): another role reaches the whole org, and the
        // grants of the former role must not wait to come back to life if the person is made a collaborator again.
        if (role != OrgRole.Collaborator)
            await ClearPropertyAccessAsync(member, cancellationToken);

        // The areas are the rental contexts the person already works in; the role decides the key of each and the
        // account membership.
        var areas = await RentalContextsOfAsync(userId, cancellationToken);
        await ProjectAsync(
            userId,
            OrgRoleCatalog.ProjectionOf(role, areas),
            [AccountContext.Key, .. areas],
            cancellationToken);

        await SaveAsync(transaction, userId, cancellationToken);

        logger.LogInformation(
            "Org member role changed: userId={UserId} orgId={OrgId} from={From} to={To}", userId, member.OrgId, previous, role);
        return member;
    }

    public async Task<OrgMember> DeactivateAsync(string userId, CancellationToken cancellationToken = default)
    {
        var (transaction, member) = await LockMemberAsync(userId, cancellationToken);
        await using var memberLock = transaction;

        if (member.Role == OrgRole.Owner)
            throw new DomainConflictException(OrgMembershipErrors.LastOwner, "OrgLastOwner");
        if (member.Status == OrgMemberStatus.Deactivated)
            return member;

        member.Status = OrgMemberStatus.Deactivated;
        member.DeactivatedAt = _clock.GetUtcNow().UtcDateTime;
        await SaveAsync(transaction, userId, cancellationToken);

        logger.LogInformation("Org member deactivated: userId={UserId} orgId={OrgId}", userId, member.OrgId);
        return member;
    }

    public async Task<OrgMember> ReactivateAsync(string userId, CancellationToken cancellationToken = default)
    {
        var (transaction, member) = await LockMemberAsync(userId, cancellationToken);
        await using var memberLock = transaction;

        if (member.Status == OrgMemberStatus.Active)
            return member;

        member.Status = OrgMemberStatus.Active;
        member.DeactivatedAt = null;
        await SaveAsync(transaction, userId, cancellationToken);

        logger.LogInformation("Org member reactivated: userId={UserId} orgId={OrgId}", userId, member.OrgId);
        return member;
    }

    public async Task RemoveAsync(string userId, CancellationToken cancellationToken = default)
    {
        var (transaction, member) = await LockMemberAsync(userId, cancellationToken);
        await using var memberLock = transaction;

        if (member.Role == OrgRole.Owner)
            throw new DomainConflictException(OrgMembershipErrors.LastOwner, "OrgLastOwner");

        // Leave no link the onboarding can reuse: EnsureOrgForUserAsync would otherwise return this org and
        // EnsureOwnerAsync would insert a second Owner beside the one that cannot be removed (UnlinkOrgAsync).
        await ClearPropertyAccessAsync(member, cancellationToken);
        db.OrgMembers.Remove(member);
        await ProjectAsync(userId, [], ManagedContexts, cancellationToken);
        await UnlinkOrgAsync(userId, member.OrgId, cancellationToken);
        await SaveAsync(transaction, userId, cancellationToken);

        logger.LogInformation("Org member removed: userId={UserId} orgId={OrgId}", userId, member.OrgId);
    }

    public async Task AbandonEmptyOrgAsync(string userId, Guid orgId, CancellationToken cancellationToken = default)
    {
        await using var transaction = await PostgresAdvisoryLocks.BeginLockedTransactionAsync(
            db, cancellationToken, OrgLock(orgId));

        // The owner row, when there is one: a legacy owner that never got it (the backfill leaves an org with several
        // candidates without a member) has only its memberships, which go the same way.
        var member = await db.OrgMembers.IgnoreQueryFilters().FirstOrDefaultAsync(m => m.UserId == userId, cancellationToken);
        if (member is not null && (member.OrgId != orgId || member.Role != OrgRole.Owner))
            throw new DomainConflictException(OrgMembershipErrors.OtherOrg, "OrgMemberOtherOrg");

        if (member is not null)
        {
            await ClearPropertyAccessAsync(member, cancellationToken);
            db.OrgMembers.Remove(member);
        }

        await ProjectAsync(userId, [], ManagedContexts, cancellationToken);
        await UnlinkOrgAsync(userId, orgId, cancellationToken);
        await SaveAsync(transaction, userId, cancellationToken);

        logger.LogInformation("Org owner left an empty org: userId={UserId} orgId={OrgId}", userId, orgId);
    }

    /// <summary>
    /// Stages the end of the member's property grants (AM-03): the <see cref="PropertyMemberAccess"/> rows go and the scope
    /// goes back to every property. For a member who leaves, changes to a role that reaches every property, or is removed. The
    /// caller saves. A member that was never restricted costs one empty read.
    /// </summary>
    private async Task ClearPropertyAccessAsync(OrgMember member, CancellationToken cancellationToken)
    {
        var grants = await db.PropertyMemberAccesses.IgnoreQueryFilters()
            .Where(a => a.UserId == member.UserId && a.OrgId == member.OrgId)
            .ToListAsync(cancellationToken);
        if (grants.Count > 0)
            db.PropertyMemberAccesses.RemoveRange(grants);

        member.PropertyScope = PropertyScope.All;
    }

    /// <summary>
    /// Stages the end of the account's link to <paramref name="orgId"/> (AM-02): <c>User.OrgId</c> is cleared, and so is the
    /// last used context when it belongs to an org (the account and the rental contexts; a supplier or staff context is
    /// not the org's). A user whose <c>OrgId</c> is another org is left alone. The caller saves.
    /// </summary>
    private async Task UnlinkOrgAsync(string userId, Guid orgId, CancellationToken cancellationToken)
    {
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == userId, cancellationToken);
        if (user is null || user.OrgId != orgId)
            return;

        user.OrgId = null;
        if (user.LastUsedContextKey is { } last && ManagedContexts.Contains(last, StringComparer.OrdinalIgnoreCase))
            user.LastUsedContextKey = null;
        user.UpdatedAt = _clock.GetUtcNow().UtcDateTime;
    }

    private OrgMember NewMember(
        string userId,
        Guid orgId,
        OrgRole role,
        string? createdByUserId,
        PropertyScope propertyScope = PropertyScope.All) => new()
        {
            OrgId = orgId,
            UserId = userId,
            Role = role,
            Status = OrgMemberStatus.Active,
            PropertyScope = propertyScope,
            CreatedAt = _clock.GetUtcNow().UtcDateTime,
            CreatedByUserId = createdByUserId,
        };

    /// <summary>
    /// Finds the member's org, takes that org's lock and reads the member again under it (tracked): what was read before
    /// the lock may have changed while waiting for it.
    /// </summary>
    private async Task<(IDbContextTransaction? Transaction, OrgMember Member)> LockMemberAsync(
        string userId,
        CancellationToken cancellationToken)
    {
        var orgId = await db.OrgMembers.IgnoreQueryFilters().AsNoTracking()
            .Where(m => m.UserId == userId)
            .Select(m => (Guid?)m.OrgId)
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw new NotFoundException($"User {userId} is not a member of any org");

        var transaction = await PostgresAdvisoryLocks.BeginLockedTransactionAsync(db, cancellationToken, OrgLock(orgId));
        var member = await db.OrgMembers.IgnoreQueryFilters().FirstOrDefaultAsync(m => m.UserId == userId, cancellationToken);
        if (member is null)
        {
            if (transaction is not null)
                await transaction.DisposeAsync();
            throw new NotFoundException($"User {userId} is not a member of any org");
        }

        return (transaction, member);
    }

    private async Task<List<string>> RentalContextsOfAsync(string userId, CancellationToken cancellationToken) =>
        await db.UserContextMemberships.AsNoTracking()
            .Where(m => m.UserId == userId && (m.ContextKey == OrgRoleCatalog.ShortRent || m.ContextKey == OrgRoleCatalog.LongRent))
            .Select(m => m.ContextKey)
            .ToListAsync(cancellationToken);

    /// <summary>
    /// Saves everything the write staged (the member row and the memberships: one <c>SaveChanges</c>, one transaction),
    /// commits the lock transaction and invalidates the user's authorization cache so the change is seen by the next
    /// request of this instance (the others converge within the cache duration).
    /// </summary>
    private async Task SaveAsync(
        IDbContextTransaction? transaction,
        string userId,
        CancellationToken cancellationToken)
    {
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            // Two orgs adding (or onboarding) the same user at once: the unique index on UserId lets one through.
            db.ChangeTracker.Clear();
            throw new DomainConflictException(OrgMembershipErrors.AlreadyMember, "OrgMemberAlreadyMember");
        }

        if (transaction is not null)
            await transaction.CommitAsync(cancellationToken);

        authorizationCache.Invalidate(userId);
    }

    /// <summary>The lock of the people of an org: taken by every write of this service, and by the callers that decide on rows these writes change.</summary>
    internal static (PostgresAdvisoryLocks.Scope Scope, string Key) OrgLock(Guid orgId) =>
        (PostgresAdvisoryLocks.Scope.OrgMembership, orgId.ToString("N"));

    private sealed record RoleRow(int Id, string ContextKey, string RoleKey);

    private async Task<Dictionary<(string ContextKey, string RoleKey), int>> LoadRoleIdsAsync(CancellationToken cancellationToken)
    {
        var rows = await db.Roles.AsNoTracking()
            .Where(r => ManagedContexts.Contains(r.ContextKey))
            .Select(r => new RoleRow(r.Id, r.ContextKey, r.RoleKey))
            .ToListAsync(cancellationToken);
        return rows.ToDictionary(r => (r.ContextKey, r.RoleKey), r => r.Id);
    }

    /// <summary>What a projection run did to one membership row.</summary>
    private sealed record ProjectionChange(ProjectionChangeKind Kind, string ContextKey, string RoleKey);

    private enum ProjectionChangeKind
    {
        Added,
        Changed,
        Removed,
    }

    /// <summary>
    /// Makes the user's memberships of <paramref name="managedContexts"/> exactly <paramref name="desired"/>: a missing
    /// row is added, a row with another role is re-pointed, a row of a managed context that is not wanted is removed.
    /// Memberships of any other context (admin, or an owner's rental rows when not managed) are never touched. Stages
    /// the changes in the change tracker; the caller saves. A wanted role that has no row is an
    /// <see cref="InvalidOperationException"/> (nothing is saved), unless <paramref name="requireRoles"/> is false: then
    /// that row is skipped with a warning (<see cref="EnsureOwnerAsync"/> only).
    /// </summary>
    private async Task<List<ProjectionChange>> ProjectAsync(
        string userId,
        IReadOnlyList<ProjectedRole> desired,
        IReadOnlyCollection<string> managedContexts,
        CancellationToken cancellationToken,
        bool requireRoles = true)
    {
        var roleIds = await LoadRoleIdsAsync(cancellationToken);
        var rows = await db.UserContextMemberships
            .Where(m => m.UserId == userId && managedContexts.Contains(m.ContextKey))
            .ToListAsync(cancellationToken);
        return ApplyProjection(userId, rows, roleIds, desired, managedContexts, requireRoles);
    }

    private List<ProjectionChange> ApplyProjection(
        string userId,
        IReadOnlyCollection<UserContextMembership> rows,
        IReadOnlyDictionary<(string ContextKey, string RoleKey), int> roleIds,
        IReadOnlyList<ProjectedRole> desired,
        IReadOnlyCollection<string> managedContexts,
        bool requireRoles = true)
    {
        var changes = new List<ProjectionChange>();
        foreach (var context in managedContexts.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var row = rows.FirstOrDefault(m => string.Equals(m.ContextKey, context, StringComparison.OrdinalIgnoreCase));
            var wanted = desired.FirstOrDefault(d => string.Equals(d.ContextKey, context, StringComparison.OrdinalIgnoreCase));

            if (wanted is null)
            {
                if (row is not null)
                {
                    db.UserContextMemberships.Remove(row);
                    changes.Add(new ProjectionChange(ProjectionChangeKind.Removed, row.ContextKey, string.Empty));
                }

                continue;
            }

            if (!roleIds.TryGetValue((wanted.ContextKey, wanted.RoleKey), out var roleId))
            {
                if (requireRoles)
                {
                    throw new InvalidOperationException(
                        $"The role {wanted.ContextKey}/{wanted.RoleKey} does not exist: the migration AddOrgMembership seeds it.");
                }

                logger.LogWarning(
                    "The role {ContextKey}/{RoleKey} does not exist: the membership of user {UserId} is not written " +
                    "(the migration AddOrgMembership seeds it; the reconcile command adds the membership afterwards)",
                    wanted.ContextKey, wanted.RoleKey, userId);
                continue;
            }

            if (row is null)
            {
                db.UserContextMemberships.Add(new UserContextMembership
                {
                    UserId = userId,
                    ContextKey = wanted.ContextKey,
                    RoleId = roleId,
                });
                changes.Add(new ProjectionChange(ProjectionChangeKind.Added, wanted.ContextKey, wanted.RoleKey));
            }
            else if (row.RoleId != roleId)
            {
                row.RoleId = roleId;
                changes.Add(new ProjectionChange(ProjectionChangeKind.Changed, wanted.ContextKey, wanted.RoleKey));
            }
        }

        return changes;
    }
}
