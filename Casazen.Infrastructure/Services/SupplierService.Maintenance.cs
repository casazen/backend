using System.Text.Json;
using System.Text.Json.Nodes;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Casazen.Infrastructure.Services;

/// <summary>
/// Admin repair <c>POST /api/admin/suppliers/fix-orphaned</c> (SU-14, A4-22): merges supplier profiles that share an email
/// without ever losing their service requests or leaving accounts linked to a deleted org, and never links an account
/// by email (A4-23). Runbook: <c>docs/runbooks/suppliers.md</c> section 9. The migration <c>SupplierProfileEmailUnique</c>
/// applies the same merge rules in SQL before creating the unique email index: keep the two in step.
/// </summary>
public partial class SupplierService
{
    /// <summary>Codes of <see cref="SupplierManualIntervention"/> (stable, listed in the runbook).</summary>
    internal static class SupplierRepairCodes
    {
        /// <summary>Profiles of one email held by different accounts: which account keeps the supplier is a decision.</summary>
        public const string SeveralAccounts = "supplier_duplicate_several_accounts";

        /// <summary>A profile of the email is suspended: a merge could lift the suspension.</summary>
        public const string Suspended = "supplier_duplicate_suspended";

        /// <summary>A concurrent change or a constraint stopped the merge of the group: nothing of it was saved.</summary>
        public const string MergeConflict = "supplier_duplicate_merge_conflict";

        /// <summary>A profile held by no account and accounts with its email: the email is not proven, no link is made.</summary>
        public const string ClaimRequired = "supplier_link_requires_claim";
    }

    private static readonly EventId SupplierRepairEvent = new(4_140, "SupplierRepair");

    public async Task<FixOrphanedSupplierOrgsReport> FixOrphanedSupplierOrgsAsync(
        bool dryRun,
        CancellationToken cancellationToken = default)
    {
        // Bulk updates, savepoints and advisory locks: the repair runs only on PostgreSQL, like the app.
        if (!PostgresAdvisoryLocks.IsSupported(db))
            throw new NotSupportedException("The supplier repair requires PostgreSQL.");

        try
        {
            return await RunSupplierRepairAsync(dryRun, cancellationToken);
        }
        catch (Exception ex) when (IsConcurrentChange(ex))
        {
            logger.LogWarning(
                SupplierRepairEvent, ex,
                "Supplier repair (dryRun={DryRun}) stopped by a concurrent change; nothing was saved", dryRun);
            throw new DomainConflictException("supplier_maintenance_conflict", "SupplierMaintenanceConflict");
        }
    }

    private async Task<FixOrphanedSupplierOrgsReport> RunSupplierRepairAsync(bool dryRun, CancellationToken cancellationToken)
    {
        // One run at a time; everything below happens in this transaction, rolled back by a dry run.
        await using var transaction = await PostgresAdvisoryLocks.BeginLockedTransactionAsync(
                db, cancellationToken, (PostgresAdvisoryLocks.Scope.SupplierMaintenance, "fix-orphaned"))
            ?? throw new InvalidOperationException("The supplier repair must own its transaction.");

        var profiles = await db.SupplierProfiles.AsNoTracking()
            .Select(sp => new RepairProfile(sp.OrgId, sp.Email, sp.Status, sp.CreatedAt))
            .ToListAsync(cancellationToken);

        // Blank email is not an identity: those profiles are never merged (nor unique in the index).
        var groups = profiles
            .Where(p => SupplierProfileEmailIndex.Normalize(p.Email).Length > 0)
            .GroupBy(p => SupplierProfileEmailIndex.Normalize(p.Email), StringComparer.Ordinal)
            .Where(g => g.Count() > 1)
            .Select(g => g.Select(p => p.OrgId).ToList())
            .OrderBy(g => profiles.Where(p => g.Contains(p.OrgId)).Min(p => p.CreatedAt))
            .ToList();

        var merges = new List<SupplierDuplicateMerge>();
        var manual = new List<SupplierManualIntervention>();
        for (var i = 0; i < groups.Count; i++)
            await MergeDuplicateGroupAsync(transaction, groups[i], i + 1, dryRun, merges, manual, cancellationToken);

        var dangling = await ClearDanglingSupplierLinksAsync(dryRun, cancellationToken);
        var backfilled = await BackfillSupplierLinksAsync(dryRun, cancellationToken);
        var orphans = await ReportUnheldProfilesAsync(manual, cancellationToken);

        if (dryRun)
        {
            await transaction.RollbackAsync(cancellationToken);
            db.ChangeTracker.Clear();
        }
        else
        {
            await transaction.CommitAsync(cancellationToken);
        }

        var report = new FixOrphanedSupplierOrgsReport(
            dryRun, profiles.Count, groups.Count, merges, dangling, backfilled, orphans, manual);
        logger.LogInformation(
            SupplierRepairEvent,
            "Supplier repair {Outcome}: profiles={Profiles}, duplicateGroups={Groups}, merged={Merged}, " +
            "serviceRequestsMoved={Requests}, danglingLinksCleared={Dangling}, linksBackfilled={Backfilled}, " +
            "orphanProfiles={Orphans}, manualInterventions={Manual}",
            dryRun ? "dry run (rolled back)" : "applied",
            report.ProfilesScanned,
            report.DuplicateGroups,
            merges.Count,
            merges.Sum(m => m.ServiceRequestsMoved),
            dangling.Count,
            backfilled.Count,
            orphans.Count,
            manual.Count);
        return report;
    }

    /// <summary>
    /// Merges one group of profiles with the same email into its keeper, or reports why it is left alone. The group is
    /// merged entirely or not at all (savepoint).
    /// </summary>
    private async Task MergeDuplicateGroupAsync(
        IDbContextTransaction transaction,
        IReadOnlyCollection<Guid> groupOrgIds,
        int groupNumber,
        bool dryRun,
        List<SupplierDuplicateMerge> merges,
        List<SupplierManualIntervention> manual,
        CancellationToken cancellationToken)
    {
        var orgIds = groupOrgIds.Order().ToArray();

        // A claim of one of these profiles (SU-02) takes the same per-profile lock: it waits for this transaction, and
        // after the merge it finds its profile gone instead of linking an account to a deleted org.
        await PostgresAdvisoryLocks.BeginLockedTransactionAsync(
            db,
            cancellationToken,
            orgIds.Select(id => (PostgresAdvisoryLocks.Scope.SupplierClaim, id.ToString("N"))).ToArray());

        // Read after the locks, so a claim committed meanwhile counts.
        var profiles = await db.SupplierProfiles.AsNoTracking()
            .Where(sp => orgIds.Contains(sp.OrgId))
            .Select(sp => new RepairProfile(sp.OrgId, sp.Email, sp.Status, sp.CreatedAt))
            .ToListAsync(cancellationToken);
        if (profiles.Count < 2)
            return;

        // Users has no tenant filter (allow-listed identity table): the repair spans every org on purpose.
        var holders = await db.Users.AsNoTracking()
            .Where(u => (u.SupplierOrgId != null && orgIds.Contains(u.SupplierOrgId.Value))
                        || (u.OrgId != null && orgIds.Contains(u.OrgId.Value)))
            .Select(u => new { u.Id, u.OrgId, u.SupplierOrgId })
            .ToListAsync(cancellationToken);
        var heldOrgIds = orgIds
            .Where(id => holders.Any(h => h.OrgId == id || h.SupplierOrgId == id))
            .ToHashSet();
        var accountIds = holders.Select(h => h.Id).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();

        if (profiles.Any(p => p.Status == SupplierStatus.Suspended))
        {
            ReportManual(manual, SupplierRepairCodes.Suspended, orgIds, accountIds);
            return;
        }

        // Two accounts that each hold a profile of the email: joining them in one org would show each one the other's
        // requests, and neither proved the email (A4-23). Somebody has to decide.
        if (heldOrgIds.Count > 1 && accountIds.Count > 1)
        {
            ReportManual(manual, SupplierRepairCodes.SeveralAccounts, orgIds, accountIds);
            return;
        }

        // Keeper: the active profile, then the one an account holds, then the oldest (stable on the id).
        var keeper = profiles
            .OrderByDescending(p => p.Status == SupplierStatus.Active)
            .ThenByDescending(p => heldOrgIds.Contains(p.OrgId))
            .ThenBy(p => p.CreatedAt)
            .ThenBy(p => p.OrgId)
            .First();
        var duplicates = profiles
            .Where(p => p.OrgId != keeper.OrgId)
            .OrderBy(p => p.CreatedAt)
            .ThenBy(p => p.OrgId)
            .ToList();

        var savepoint = $"supplier_group_{groupNumber}";
        await transaction.CreateSavepointAsync(savepoint, cancellationToken);
        var groupMerges = new List<SupplierDuplicateMerge>();
        try
        {
            foreach (var duplicate in duplicates)
                groupMerges.Add(await MergeIntoKeeperAsync(transaction, keeper.OrgId, duplicate.OrgId, cancellationToken));
            await transaction.ReleaseSavepointAsync(savepoint, cancellationToken);
        }
        catch (Exception ex) when (IsConcurrentChange(ex))
        {
            await transaction.RollbackToSavepointAsync(savepoint, cancellationToken);
            db.ChangeTracker.Clear();
            logger.LogWarning(
                SupplierRepairEvent, ex,
                "Supplier duplicate group {OrgIds} not merged ({Code}): a concurrent change or a constraint stopped it",
                string.Join(',', orgIds), SupplierRepairCodes.MergeConflict);
            ReportManual(manual, SupplierRepairCodes.MergeConflict, orgIds, accountIds);
            return;
        }

        merges.AddRange(groupMerges);
        foreach (var merge in groupMerges)
        {
            logger.Log(
                dryRun ? LogLevel.Information : LogLevel.Warning,
                SupplierRepairEvent,
                "Supplier duplicate {DuplicateOrgId} {Action} into {KeeperOrgId}: serviceRequests={ServiceRequests}, " +
                "availabilityMoved={AvailabilityMoved}, " +
                "availabilityDropped={AvailabilityDropped}, categoriesAdded={CategoriesAdded}, comuniAdded={ComuniAdded}, " +
                "supplierLinks={SupplierLinks}, orgMembers={OrgMembers}, devices={Devices}, " +
                "duplicateOrgDeleted={DuplicateOrgDeleted}",
                merge.DuplicateOrgId,
                dryRun ? "would be merged (dry run)" : "merged",
                merge.KeeperOrgId,
                merge.ServiceRequestsMoved,
                merge.AvailabilityDaysMoved,
                merge.AvailabilityDaysDropped,
                merge.CategoriesAdded.Count,
                merge.ComuniAdded.Count,
                merge.SupplierLinksMoved,
                merge.OrgMembersMoved,
                merge.DevicesMoved,
                merge.DuplicateOrgDeleted);
        }
    }

    private async Task<SupplierDuplicateMerge> MergeIntoKeeperAsync(
        IDbContextTransaction transaction,
        Guid keeperId,
        Guid duplicateId,
        CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;

        // ServiceRequests (host org filter only by explicit scopes) and SupplierAvailability have no tenant filter: the
        // supplier side of the duplicate moves whatever host org the rows belong to.
        var requests = await db.ServiceRequests
            .Where(sr => sr.SupplierOrgId == duplicateId)
            .ExecuteUpdateAsync(set => set.SetProperty(sr => sr.SupplierOrgId, keeperId), cancellationToken);

        // A day the keeper already has keeps the keeper's value: the keeper is the profile in use.
        var daysMoved = await db.SupplierAvailability
            .Where(a => a.OrgId == duplicateId
                        && !db.SupplierAvailability.Any(k => k.OrgId == keeperId && k.Date == a.Date))
            .ExecuteUpdateAsync(set => set.SetProperty(a => a.OrgId, keeperId), cancellationToken);
        var daysDropped = await db.SupplierAvailability.CountAsync(a => a.OrgId == duplicateId, cancellationToken);

        var keeper = await db.SupplierProfiles.FirstAsync(sp => sp.OrgId == keeperId, cancellationToken);
        var duplicate = await db.SupplierProfiles.AsNoTracking().FirstAsync(sp => sp.OrgId == duplicateId, cancellationToken);
        var (categoriesJson, categoriesAdded) = AppendMissingStrings(keeper.CategoriesJson, duplicate.CategoriesJson);
        var (comuniJson, comuniAdded) = AppendMissingStrings(keeper.ComuniJson, duplicate.ComuniJson);
        if (categoriesAdded.Count > 0 || comuniAdded.Count > 0)
        {
            keeper.CategoriesJson = categoriesJson;
            keeper.ComuniJson = comuniJson;
            keeper.UpdatedAt = now;
            await db.SaveChangesAsync(cancellationToken);
        }

        // Accounts of the duplicate reach the keeper. A supplier-only account (OrgId = duplicate, no SupplierOrgId) gets
        // the explicit link too, so it keeps the supplier console even when the duplicate org stays below.
        var supplierLinks = await db.Users
            .Where(u => u.SupplierOrgId == duplicateId || (u.OrgId == duplicateId && u.SupplierOrgId == null))
            .ExecuteUpdateAsync(
                set => set.SetProperty(u => u.SupplierOrgId, keeperId).SetProperty(u => u.UpdatedAt, now),
                cancellationToken);

        // The availability days the keeper already had go with the profile (ON DELETE CASCADE).
        await db.SupplierProfiles.Where(sp => sp.OrgId == duplicateId).ExecuteDeleteAsync(cancellationToken);

        var (orgMembers, devices, orgDeleted) =
            await RemoveDuplicateOrgAsync(transaction, keeperId, duplicateId, now, cancellationToken);

        return new SupplierDuplicateMerge(
            keeperId,
            duplicateId,
            requests,
            daysMoved,
            daysDropped,
            categoriesAdded,
            comuniAdded,
            supplierLinks,
            orgMembers,
            devices,
            orgDeleted);
    }

    /// <summary>
    /// Deletes the duplicate org once its members and devices are on the keeper. An org that also holds host data
    /// (it is not a supplier org, or has consents, a signup attribution, properties, bookings...) is kept without its
    /// supplier profile: its accounts keep it as <c>OrgId</c> and reach the keeper through <c>SupplierOrgId</c>.
    /// </summary>
    private async Task<(int OrgMembers, int Devices, bool Deleted)> RemoveDuplicateOrgAsync(
        IDbContextTransaction transaction,
        Guid keeperId,
        Guid duplicateId,
        DateTime now,
        CancellationToken cancellationToken)
    {
        // ConsentRecords and SignupAttributions are tenant-owned: this platform-admin repair looks at the duplicate org
        // explicitly, whatever the caller's org.
        var hasHostData =
            await db.Orgs.AnyAsync(o => o.Id == duplicateId && o.OrgType != OrgType.Supplier, cancellationToken)
            || await db.ConsentRecords.IgnoreQueryFilters([AppDbContext.TenantQueryFilter])
                .AnyAsync(c => c.OrgId == duplicateId, cancellationToken)
            || await db.SignupAttributions.IgnoreQueryFilters([AppDbContext.TenantQueryFilter])
                .AnyAsync(a => a.OrgId == duplicateId, cancellationToken);
        if (hasHostData)
            return (0, 0, false);

        const string savepoint = "supplier_duplicate_org";
        await transaction.CreateSavepointAsync(savepoint, cancellationToken);
        try
        {
            var orgMembers = await db.Users
                .Where(u => u.OrgId == duplicateId)
                .ExecuteUpdateAsync(
                    set => set.SetProperty(u => u.OrgId, keeperId).SetProperty(u => u.UpdatedAt, now),
                    cancellationToken);
            var devices = await db.DeviceRegistrations
                .Where(d => d.OrgId == duplicateId)
                .ExecuteUpdateAsync(set => set.SetProperty(d => d.OrgId, keeperId), cancellationToken);
            await db.Orgs.Where(o => o.Id == duplicateId).ExecuteDeleteAsync(cancellationToken);
            await transaction.ReleaseSavepointAsync(savepoint, cancellationToken);
            return (orgMembers, devices, true);
        }
        catch (Exception ex) when (FindPostgresError(ex)?.SqlState == PostgresErrorCodes.ForeignKeyViolation)
        {
            // Host rows (properties, bookings, ...) still reference the org (ON DELETE RESTRICT).
            await transaction.RollbackToSavepointAsync(savepoint, cancellationToken);
            return (0, 0, false);
        }
    }

    /// <summary>Accounts whose <c>SupplierOrgId</c> points to an org that no longer exists are unlinked.</summary>
    private async Task<IReadOnlyList<string>> ClearDanglingSupplierLinksAsync(bool dryRun, CancellationToken cancellationToken)
    {
        var userIds = await db.Users
            .Where(u => u.SupplierOrgId != null && !db.Orgs.Any(o => o.Id == u.SupplierOrgId))
            .OrderBy(u => u.Id)
            .Select(u => u.Id)
            .ToListAsync(cancellationToken);
        if (userIds.Count == 0)
            return userIds;

        var now = DateTime.UtcNow;
        await db.Users
            .Where(u => userIds.Contains(u.Id))
            .ExecuteUpdateAsync(
                set => set.SetProperty(u => u.SupplierOrgId, (Guid?)null).SetProperty(u => u.UpdatedAt, now),
                cancellationToken);
        foreach (var userId in userIds)
        {
            logger.Log(
                dryRun ? LogLevel.Information : LogLevel.Warning,
                SupplierRepairEvent,
                "Supplier link of user {UserId} to a deleted org {Action}",
                userId,
                dryRun ? "would be cleared (dry run)" : "cleared");
        }

        return userIds;
    }

    /// <summary>Accounts whose <c>OrgId</c> is a supplier org with a profile get it as <c>SupplierOrgId</c> (their own link).</summary>
    private async Task<IReadOnlyList<string>> BackfillSupplierLinksAsync(bool dryRun, CancellationToken cancellationToken)
    {
        var userIds = await db.Users
            .Where(u => u.SupplierOrgId == null
                        && u.OrgId != null
                        && db.Orgs.Any(o => o.Id == u.OrgId && o.OrgType == OrgType.Supplier)
                        && db.SupplierProfiles.Any(sp => sp.OrgId == u.OrgId))
            .OrderBy(u => u.Id)
            .Select(u => u.Id)
            .ToListAsync(cancellationToken);
        if (userIds.Count == 0)
            return userIds;

        var now = DateTime.UtcNow;
        await db.Users
            .Where(u => userIds.Contains(u.Id))
            .ExecuteUpdateAsync(
                set => set.SetProperty(u => u.SupplierOrgId, u => u.OrgId).SetProperty(u => u.UpdatedAt, now),
                cancellationToken);
        foreach (var userId in userIds)
        {
            logger.Log(
                dryRun ? LogLevel.Information : LogLevel.Warning,
                SupplierRepairEvent,
                "Supplier link of user {UserId} {Action} from its own supplier org",
                userId,
                dryRun ? "would be set (dry run)" : "set");
        }

        return userIds;
    }

    /// <summary>
    /// Profiles that no account holds. Nothing is linked (A4-23): an email match is not proof, and the admin repair has
    /// neither a claim token nor Auth0's <c>email_verified</c>. When accounts with the same email exist the case is
    /// reported (<see cref="SupplierRepairCodes.ClaimRequired"/>): the supplier links the profile with the claim (SU-02).
    /// </summary>
    private async Task<IReadOnlyList<Guid>> ReportUnheldProfilesAsync(
        List<SupplierManualIntervention> manual,
        CancellationToken cancellationToken)
    {
        var unheld = await db.SupplierProfiles.AsNoTracking()
            .Where(sp => !db.Users.Any(u => u.SupplierOrgId == sp.OrgId || u.OrgId == sp.OrgId))
            .OrderBy(sp => sp.CreatedAt)
            .ThenBy(sp => sp.OrgId)
            .Select(sp => new { sp.OrgId, sp.Email })
            .ToListAsync(cancellationToken);

        var emails = unheld
            .Select(p => SupplierProfileEmailIndex.Normalize(p.Email))
            .Where(e => e.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        var accounts = emails.Count == 0
            ? []
            : await db.Users.AsNoTracking()
                .Where(u => emails.Contains(u.Email.Trim().ToLower()))
                .Select(u => new { u.Id, u.Email })
                .ToListAsync(cancellationToken);

        var orphans = new List<Guid>();
        foreach (var profile in unheld)
        {
            var email = SupplierProfileEmailIndex.Normalize(profile.Email);
            var accountIds = email.Length == 0
                ? []
                : accounts
                    .Where(a => SupplierProfileEmailIndex.Normalize(a.Email) == email)
                    .Select(a => a.Id)
                    .Order(StringComparer.Ordinal)
                    .ToList();
            if (accountIds.Count == 0)
                orphans.Add(profile.OrgId);
            else
                ReportManual(manual, SupplierRepairCodes.ClaimRequired, [profile.OrgId], accountIds);
        }

        return orphans;
    }

    private void ReportManual(
        List<SupplierManualIntervention> manual,
        string code,
        IReadOnlyList<Guid> orgIds,
        IReadOnlyList<string> userIds)
    {
        manual.Add(new SupplierManualIntervention(code, orgIds, userIds));
        logger.LogWarning(
            SupplierRepairEvent,
            "Supplier repair needs a manual intervention ({Code}): orgs {OrgIds}, {Accounts} account(s) {UserIds}; nothing changed",
            code,
            string.Join(',', orgIds),
            userIds.Count,
            string.Join(',', userIds));
    }

    /// <summary>
    /// Appends to the keeper's JSON array the string items of the duplicate's array it does not have (ordinal, first
    /// occurrence, in the duplicate's order). The keeper's array is otherwise kept as is; a value that is not an array
    /// counts as empty. Same rule as the migration SQL.
    /// </summary>
    internal static (string Json, IReadOnlyList<string> Added) AppendMissingStrings(string? keeperJson, string? duplicateJson)
    {
        var existing = DeserializeStrings(keeperJson);
        var added = DeserializeStrings(duplicateJson)
            .Distinct(StringComparer.Ordinal)
            .Where(value => !existing.Contains(value, StringComparer.Ordinal))
            .ToList();
        if (added.Count == 0)
            return (keeperJson ?? "[]", added);

        JsonArray array;
        try
        {
            array = JsonNode.Parse(keeperJson ?? "[]") as JsonArray ?? [];
        }
        catch (JsonException)
        {
            array = [];
        }

        foreach (var value in added)
            array.Add(JsonValue.Create(value));
        return (array.ToJsonString(JsonOpts), added);
    }

    /// <summary>Unique or foreign-key violation, serialization failure or deadlock: someone changed the rows meanwhile.</summary>
    private static bool IsConcurrentChange(Exception ex) =>
        FindPostgresError(ex)?.SqlState is PostgresErrorCodes.UniqueViolation
            or PostgresErrorCodes.ForeignKeyViolation
            or PostgresErrorCodes.SerializationFailure
            or PostgresErrorCodes.DeadlockDetected;

    private static PostgresException? FindPostgresError(Exception? ex)
    {
        for (var current = ex; current is not null; current = current.InnerException)
        {
            if (current is PostgresException postgres)
                return postgres;
        }

        return null;
    }

    private sealed record RepairProfile(Guid OrgId, string Email, SupplierStatus Status, DateTime CreatedAt);
}
