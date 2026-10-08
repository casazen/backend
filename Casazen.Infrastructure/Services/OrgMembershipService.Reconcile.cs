using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Casazen.Infrastructure.Services;

/// <summary>
/// The reconcile command of the org membership (<c>POST /api/admin/org-members/reconcile</c>, AM-01). Two jobs, both
/// idempotent: give an <see cref="OrgMember"/> owner row to the host orgs that have none (the same rule as the backfill
/// of the migration <c>AddOrgMembership</c>: <c>OrgMembershipBackfill</c>), then re-align the memberships with the org
/// roles. It decides nothing that is a decision: ambiguous orgs and inconsistencies are reported and left alone.
/// Runbook: <c>docs/runbooks/org-team.md</c>.
/// </summary>
public sealed partial class OrgMembershipService
{
    private static readonly EventId ReconcileEvent = new(4_401, "OrgMembershipReconcile");

    /// <summary>Stable code of the 409 when a concurrent change stopped the run (nothing was saved).</summary>
    internal const string MaintenanceConflictCode = "org_membership_maintenance_conflict";

    public async Task<OrgMembershipReconcileReport> ReconcileAsync(bool dryRun, CancellationToken cancellationToken = default)
    {
        try
        {
            return await RunReconcileAsync(dryRun, cancellationToken);
        }
        catch (Exception ex) when (IsConcurrentChange(ex))
        {
            db.ChangeTracker.Clear();
            logger.LogWarning(
                ReconcileEvent, ex,
                "Org membership reconcile (dryRun={DryRun}) stopped by a concurrent change; nothing was saved", dryRun);
            throw new DomainConflictException(MaintenanceConflictCode, "OrgMembershipMaintenanceConflict");
        }
    }

    private static bool IsConcurrentChange(Exception ex) =>
        ex is DbUpdateConcurrencyException
        || ex is DbUpdateException { InnerException: PostgresException { SqlState: PostgresErrorCodes.UniqueViolation or PostgresErrorCodes.SerializationFailure or PostgresErrorCodes.DeadlockDetected } };

    /// <summary>A user of a host org, as the reconcile reads it.</summary>
    private sealed record HostUser(string Id, Guid OrgId, UserRole Role);

    private async Task<OrgMembershipReconcileReport> RunReconcileAsync(bool dryRun, CancellationToken cancellationToken)
    {
        // One run at a time; everything below is staged in the change tracker and saved once at the end (one
        // transaction), or dropped by a dry run.
        await using var transaction = await PostgresAdvisoryLocks.BeginLockedTransactionAsync(
            db, cancellationToken, (PostgresAdvisoryLocks.Scope.OrgMembershipMaintenance, "reconcile"));

        var fixes = new List<OrgMembershipFix>();
        var issues = new List<OrgMembershipIssue>();
        var touchedUsers = new HashSet<string>(StringComparer.Ordinal);

        var roleIds = await LoadRoleIdsAsync(cancellationToken);

        // IgnoreQueryFilters: the command acts on every org, whatever the tenant of the admin who runs it.
        var members = await db.OrgMembers.IgnoreQueryFilters().ToListAsync(cancellationToken);
        var memberByUser = members.ToDictionary(m => m.UserId, StringComparer.Ordinal);

        // ── 1. The owner of every host org that has no org member ────────────────────────────────────────────────
        // Only a Host org: a supplier org may hold several accounts and is no org team (PL-05).
        var hostUsers = await db.Users.AsNoTracking()
            .Where(u => u.OrgId != null && u.Org!.OrgType == OrgType.Host)
            .Select(u => new HostUser(u.Id, u.OrgId!.Value, u.Role))
            .ToListAsync(cancellationToken);

        var ownerHostUserIds = (await db.UserContextMemberships.AsNoTracking()
                .Where(m =>
                    (m.ContextKey == OrgRoleCatalog.ShortRent && m.Role.RoleKey == OrgRoleCatalog.ShortRentOwnerRoleKey)
                    || (m.ContextKey == OrgRoleCatalog.LongRent && m.Role.RoleKey == OrgRoleCatalog.LongRentOwnerRoleKey))
                .Select(m => m.UserId)
                .Distinct()
                .ToListAsync(cancellationToken))
            .ToHashSet(StringComparer.Ordinal);

        var hostOrgs = hostUsers.GroupBy(u => u.OrgId).OrderBy(g => g.Key).ToList();
        foreach (var org in hostOrgs)
        {
            var candidates = org.Where(u => IsOwnerLike(u, ownerHostUserIds, memberByUser)).ToList();
            var others = org
                .Where(u => !candidates.Contains(u) && !memberByUser.ContainsKey(u.Id))
                .Select(u => u.Id)
                .Order(StringComparer.Ordinal)
                .ToList();

            if (candidates.Count > 1)
            {
                // Several people look like the owner: which one is it is a decision, never a guess.
                var ids = candidates.Select(u => u.Id).Order(StringComparer.Ordinal).ToList();
                issues.Add(new OrgMembershipIssue(
                    OrgMembershipReconcileCodes.OwnerAmbiguous,
                    org.Key,
                    ids,
                    $"{ids.Count} users of the org look like its owner: no org member was created."));
                logger.LogWarning(
                    ReconcileEvent,
                    "Org {OrgId} has {Count} owner candidates ({UserIds}): no org member created",
                    org.Key, ids.Count, string.Join(", ", ids));
            }
            else if (candidates.Count == 1 && !memberByUser.ContainsKey(candidates[0].Id))
            {
                var owner = NewMember(candidates[0].Id, org.Key, OrgRole.Owner, createdByUserId: null);
                db.OrgMembers.Add(owner);
                memberByUser[owner.UserId] = owner;
                fixes.Add(new OrgMembershipFix(
                    OrgMembershipReconcileCodes.OwnerCreated, owner.UserId, org.Key, "Owner, all properties."));
                touchedUsers.Add(owner.UserId);
            }

            if (others.Count > 0)
            {
                issues.Add(new OrgMembershipIssue(
                    OrgMembershipReconcileCodes.UserWithoutMember,
                    org.Key,
                    others,
                    $"{others.Count} user(s) of the org have no org member: their role in the org is not guessed."));
            }
        }

        foreach (var org in memberByUser.Values
                     .Where(m => m.Role == OrgRole.Owner)
                     .GroupBy(m => m.OrgId)
                     .Where(g => g.Count() > 1)
                     .OrderBy(g => g.Key))
        {
            var ids = org.Select(m => m.UserId).Order(StringComparer.Ordinal).ToList();
            issues.Add(new OrgMembershipIssue(
                OrgMembershipReconcileCodes.SeveralOwners, org.Key, ids, $"{ids.Count} org members of the org are owners."));
        }

        // ── 2. The memberships follow the org roles ─────────────────────────────────────────────────────────────
        var memberUserIds = memberByUser.Keys.ToList();
        var userOrgs = (await db.Users.AsNoTracking()
                .Where(u => memberUserIds.Contains(u.Id))
                .Select(u => new { u.Id, u.OrgId })
                .ToListAsync(cancellationToken))
            .ToDictionary(u => u.Id, u => u.OrgId, StringComparer.Ordinal);

        var memberships = await db.UserContextMemberships
            .Where(m => ManagedContexts.Contains(m.ContextKey))
            .ToListAsync(cancellationToken);
        var membershipsByUser = memberships.ToLookup(m => m.UserId, StringComparer.Ordinal);

        foreach (var member in memberByUser.Values.OrderBy(m => m.UserId, StringComparer.Ordinal))
        {
            if (userOrgs.GetValueOrDefault(member.UserId) != member.OrgId)
            {
                // The user's org is the one of the request: a member of another org than its user's is a decision.
                issues.Add(new OrgMembershipIssue(
                    OrgMembershipReconcileCodes.OrgMismatch,
                    member.OrgId,
                    [member.UserId],
                    "The org member's org is not the user's org: not aligned."));
                continue;
            }

            var rows = membershipsByUser[member.UserId].ToList();
            var rentalRows = rows.Where(r => OrgRoleCatalog.IsRentalContext(r.ContextKey)).ToList();

            List<ProjectedRole> desired = [];
            List<string> managed = [AccountContext.Key];
            if (OrgRoleCatalog.AccountRoleKey(member.Role) is { } accountKey)
                desired.Add(new ProjectedRole(AccountContext.Key, accountKey));

            if (member.Role == OrgRole.Owner)
            {
                // The owner's rental rows come from the onboarding: kept as they are, only reported when there are none.
                if (rentalRows.Count == 0)
                {
                    issues.Add(new OrgMembershipIssue(
                        OrgMembershipReconcileCodes.OwnerWithoutHostMembership,
                        member.OrgId,
                        [member.UserId],
                        "The owner has no short-rent or long-rent membership: its rental contexts may come from the token only."));
                }
            }
            else
            {
                // The areas are the rental contexts the member already works in: only the role key of each is aligned.
                foreach (var row in rentalRows)
                {
                    desired.Add(new ProjectedRole(row.ContextKey, OrgRoleCatalog.HostRoleKey(member.Role, row.ContextKey)));
                    managed.Add(row.ContextKey);
                }

                if (rentalRows.Count == 0)
                {
                    issues.Add(new OrgMembershipIssue(
                        OrgMembershipReconcileCodes.MemberWithoutHostMembership,
                        member.OrgId,
                        [member.UserId],
                        "The member has no short-rent or long-rent membership: its areas are unknown, nothing was added."));
                }
            }

            foreach (var change in ApplyProjection(member.UserId, rows, roleIds, desired, managed))
            {
                fixes.Add(ToFix(change, member));
                touchedUsers.Add(member.UserId);
            }
        }

        // An account membership belongs to an org member: one left without it is a leftover (the member was removed).
        foreach (var leftover in memberships.Where(m => AccountContext.IsAccountContext(m.ContextKey) && !memberByUser.ContainsKey(m.UserId)))
        {
            db.UserContextMemberships.Remove(leftover);
            fixes.Add(new OrgMembershipFix(
                OrgMembershipReconcileCodes.AccountMembershipRemoved,
                leftover.UserId,
                null,
                "Account membership of a user that is not an org member."));
            touchedUsers.Add(leftover.UserId);
        }

        // ── 3. Save, or drop the plan ────────────────────────────────────────────────────────────────────────────
        if (dryRun)
        {
            db.ChangeTracker.Clear();
        }
        else
        {
            await db.SaveChangesAsync(cancellationToken);
            if (transaction is not null)
                await transaction.CommitAsync(cancellationToken);
            foreach (var userId in touchedUsers)
                authorizationCache.Invalidate(userId);
        }

        var report = new OrgMembershipReconcileReport(dryRun, hostOrgs.Count, memberByUser.Count, fixes, issues);
        logger.LogInformation(
            ReconcileEvent,
            "Org membership reconcile {Outcome}: hostOrgs={HostOrgs}, members={Members}, fixes={Fixes}, issues={Issues}",
            dryRun ? "dry run (nothing saved)" : "applied",
            report.HostOrgsScanned,
            report.MembersScanned,
            fixes.Count,
            issues.Count);
        return report;
    }

    /// <summary>
    /// The owner of a host org by what is stored, never by a token. Whoever already has an org member row is what that
    /// row says (the source of truth: an administrator that still holds an owner's rental row is drift to align, not a
    /// second owner, so one run converges). Whoever has none is an owner by the user role of an owner
    /// (<c>PropertyOwner</c>, <c>LongTermLandlord</c>) or by an owner's host membership (<c>property_owner</c>,
    /// <c>long_term_landlord</c>), which is how a platform admin that set up its own org shows. For users without an org
    /// member row it is the same rule as the SQL backfill of the migration.
    /// </summary>
    private static bool IsOwnerLike(
        HostUser user,
        IReadOnlySet<string> ownerHostUserIds,
        IReadOnlyDictionary<string, OrgMember> memberByUser) =>
        memberByUser.TryGetValue(user.Id, out var member)
            ? member.Role == OrgRole.Owner
            : user.Role is UserRole.PropertyOwner or UserRole.LongTermLandlord || ownerHostUserIds.Contains(user.Id);

    private static OrgMembershipFix ToFix(ProjectionChange change, OrgMember member)
    {
        var account = AccountContext.IsAccountContext(change.ContextKey);
        var (code, detail) = change.Kind switch
        {
            ProjectionChangeKind.Added => (OrgMembershipReconcileCodes.AccountMembershipAdded, $"{change.ContextKey}/{change.RoleKey} added."),
            ProjectionChangeKind.Removed => (OrgMembershipReconcileCodes.AccountMembershipRemoved, $"{change.ContextKey} membership removed: the role has none."),
            _ => (
                account ? OrgMembershipReconcileCodes.AccountMembershipChanged : OrgMembershipReconcileCodes.HostMembershipChanged,
                $"{change.ContextKey} membership set to {change.RoleKey}."),
        };
        return new OrgMembershipFix(code, member.UserId, member.OrgId, detail);
    }
}
