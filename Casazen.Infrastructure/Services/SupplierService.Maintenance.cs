using System.Text.Json;
using System.Text.Json.Nodes;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Services;
using Casazen.Core.Suppliers;
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
/// applies the same merge rules in SQL before creating the unique email index: keep the two in step. (The price catalog
/// of SP-02 and the agenda of SP-03 are created by later migrations, so that SQL has nothing to move for them: only this
/// code does, sections 19.7 and 20.8.)
/// </summary>
public partial class SupplierService
{
    internal static class SupplierRepairCodes
    {
        public const string SeveralAccounts = "supplier_duplicate_several_accounts";

        public const string Suspended = "supplier_duplicate_suspended";

        /// <summary>A concurrent change or a constraint stopped the merge of the group: nothing of it was saved.</summary>
        public const string MergeConflict = "supplier_duplicate_merge_conflict";

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
        await using var transaction = await PostgresAdvisoryLocks.BeginLockedTransactionAsync(
                db, cancellationToken, (PostgresAdvisoryLocks.Scope.SupplierMaintenance, "fix-orphaned"))
            ?? throw new InvalidOperationException("The supplier repair must own its transaction.");

        var profiles = await db.SupplierProfiles.AsNoTracking()
            .Select(sp => new RepairProfile(sp.OrgId, sp.Email, sp.Status, sp.CreatedAt))
            .ToListAsync(cancellationToken);

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

        var profiles = await db.SupplierProfiles.AsNoTracking()
            .Where(sp => orgIds.Contains(sp.OrgId))
            .Select(sp => new RepairProfile(sp.OrgId, sp.Email, sp.Status, sp.CreatedAt))
            .ToListAsync(cancellationToken);
        if (profiles.Count < 2)
            return;

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

        if (heldOrgIds.Count > 1 && accountIds.Count > 1)
        {
            ReportManual(manual, SupplierRepairCodes.SeveralAccounts, orgIds, accountIds);
            return;
        }

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
                "serviceListings={ServiceListings}, agendaRows={AgendaRows}, showcaseRows={ShowcaseRows}, duplicateOrgDeleted={DuplicateOrgDeleted}",
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
                merge.ServiceListingsMoved,
                merge.AgendaRowsMoved,
                merge.ShowcaseRowsMoved,
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

        var requests = await db.ServiceRequests
            .Where(sr => sr.SupplierOrgId == duplicateId)
            .ExecuteUpdateAsync(set => set.SetProperty(sr => sr.SupplierOrgId, keeperId), cancellationToken);

        await db.ServiceRequestPayments
            .Where(p => p.SupplierOrgId == duplicateId)
            .ExecuteUpdateAsync(
                set => set.SetProperty(p => p.SupplierOrgId, keeperId).SetProperty(p => p.UpdatedAt, now),
                cancellationToken);

        // The content of the showcase (SP-13a) is a child of the profile too: the keeper's stays, the duplicate's moves only when the
        // keeper has none, and counts with the rows of the showcase. It comes first: its lock is taken before the calendar lock of the
        // supplier (the order every change of the content follows), and the move of the showcase below takes the calendar locks.
        var showcaseContentMoved = await MoveShowcaseContentAsync(keeperId, duplicateId, now, cancellationToken);

        var showcaseRowsMoved = await MoveShowcaseAsync(keeperId, duplicateId, cancellationToken);
        showcaseRowsMoved += showcaseContentMoved;

        var daysMoved = await db.SupplierAvailability
            .Where(a => a.OrgId == duplicateId
                        && !db.SupplierAvailability.Any(k => k.OrgId == keeperId && k.Date == a.Date))
            .ExecuteUpdateAsync(set => set.SetProperty(a => a.OrgId, keeperId), cancellationToken);
        var daysDropped = await db.SupplierAvailability.CountAsync(a => a.OrgId == duplicateId, cancellationToken);

        var listingsMoved = await MoveServiceListingsAsync(keeperId, duplicateId, now, cancellationToken);

        var agendaRowsMoved = await MoveAgendaAsync(keeperId, duplicateId, now, cancellationToken);

        var keeper = await db.SupplierProfiles.FirstAsync(sp => sp.OrgId == keeperId, cancellationToken);
        var duplicate = await db.SupplierProfiles.AsNoTracking().FirstAsync(sp => sp.OrgId == duplicateId, cancellationToken);
        var (categoriesJson, categoriesAdded) = AppendMissingStrings(keeper.CategoriesJson, duplicate.CategoriesJson);
        var (comuniJson, comuniAdded) = AppendMissingStrings(keeper.ComuniJson, duplicate.ComuniJson);
        // The comuni chosen from the official ISTAT list (SU-04) move as well.
        var (istatCodesJson, istatCodesAdded) = AppendMissingStrings(keeper.ComuneIstatCodesJson, duplicate.ComuneIstatCodesJson);
        if (categoriesAdded.Count > 0 || comuniAdded.Count > 0 || istatCodesAdded.Count > 0)
        {
            keeper.CategoriesJson = categoriesJson;
            keeper.ComuniJson = comuniJson;
            keeper.ComuneIstatCodesJson = istatCodesJson;
            keeper.UpdatedAt = now;
            await db.SaveChangesAsync(cancellationToken);
        }

        var supplierLinks = await db.Users
            .Where(u => u.SupplierOrgId == duplicateId || (u.OrgId == duplicateId && u.SupplierOrgId == null))
            .ExecuteUpdateAsync(
                set => set.SetProperty(u => u.SupplierOrgId, keeperId).SetProperty(u => u.UpdatedAt, now),
                cancellationToken);

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
            orgDeleted,
            listingsMoved,
            agendaRowsMoved,
            showcaseRowsMoved);
    }

    /// <summary>
    /// Moves the public showcase of the duplicate (SP-10) to the keeper and returns the rows moved: the requests of its showcase
    /// (their <c>OrgId</c> is the duplicate supplier org, which would stay behind and keep the org alive) and its private
    /// customers. The keeper's rows stay as they are: a customer of the duplicate whose e-mail the keeper already has
    /// (the same person booked both) is merged into the keeper's customer, with its requests, and its row goes; any other moves
    /// with its requests. The unverified holds of the duplicate are dropped: they wait for an e-mail check for 30 minutes, hold
    /// the slots of a calendar that is being merged, and would only collide with the keeper's (client request id, code).
    /// </summary>
    private async Task<int> MoveShowcaseAsync(Guid keeperId, Guid duplicateId, CancellationToken cancellationToken)
    {
        await PostgresAdvisoryLocks.BeginLockedTransactionAsync(
            db,
            cancellationToken,
            CalendarSyncService.AvailabilityLock(keeperId),
            CalendarSyncService.AvailabilityLock(duplicateId));

        var moved = await db.ServiceRequests
            .Where(sr => sr.OrgId == duplicateId && sr.RentalContext == ServiceRequestRentalContext.Showcase)
            .ExecuteUpdateAsync(set => set.SetProperty(sr => sr.OrgId, keeperId), cancellationToken);

        var duplicateCustomers = await db.ServiceCustomers
            .AsNoTracking()
            .Where(c => c.OrgId == duplicateId)
            .Select(c => new { c.Id, c.EmailHash })
            .ToListAsync(cancellationToken);
        var keeperCustomers = (await db.ServiceCustomers
                .AsNoTracking()
                .Where(c => c.OrgId == keeperId)
                .Select(c => new { c.Id, c.EmailHash })
                .ToListAsync(cancellationToken))
            .ToDictionary(c => c.EmailHash, c => c.Id, StringComparer.Ordinal);

        foreach (var customer in duplicateCustomers)
        {
            var id = customer.Id;
            if (keeperCustomers.TryGetValue(customer.EmailHash, out var keeperCustomerId))
            {
                await db.ServiceRequests
                    .Where(sr => sr.CustomerId == id)
                    .ExecuteUpdateAsync(set => set.SetProperty(sr => sr.CustomerId, keeperCustomerId), cancellationToken);
                await db.ServiceCustomers
                    .Where(c => c.OrgId == duplicateId && c.Id == id)
                    .ExecuteDeleteAsync(cancellationToken);
            }
            else
            {
                await db.ServiceCustomers
                    .Where(c => c.OrgId == duplicateId && c.Id == id)
                    .ExecuteUpdateAsync(set => set.SetProperty(c => c.OrgId, keeperId), cancellationToken);
            }

            moved++;
        }

        await db.ShowcaseBookingHolds
            .Where(h => h.OrgId == duplicateId)
            .ExecuteDeleteAsync(cancellationToken);

        await db.ShowcaseInquiries
            .Where(i => i.OrgId == duplicateId)
            .ExecuteDeleteAsync(cancellationToken);
        await db.ServiceRequestMessages
            .Where(m => m.OrgId == duplicateId)
            .ExecuteUpdateAsync(set => set.SetProperty(m => m.OrgId, keeperId), cancellationToken);

        return moved;
    }

    private async Task<int> MoveServiceListingsAsync(
        Guid keeperId,
        Guid duplicateId,
        DateTime now,
        CancellationToken cancellationToken)
    {
        await PostgresAdvisoryLocks.BeginLockedTransactionAsync(
            db,
            cancellationToken,
            (PostgresAdvisoryLocks.Scope.SupplierServiceCatalog, keeperId.ToString("N")),
            (PostgresAdvisoryLocks.Scope.SupplierServiceCatalog, duplicateId.ToString("N")));

        var duplicateListings = await db.SupplierServiceListings
            .AsNoTracking()
            .Where(l => l.OrgId == duplicateId)
            .OrderBy(l => l.CreatedAt)
            .ThenBy(l => l.Id)
            .Select(l => new { l.Id, l.Slug, l.DeletedAt })
            .ToListAsync(cancellationToken);
        if (duplicateListings.Count == 0)
            return 0;

        var taken = (await db.SupplierServiceListings
                .AsNoTracking()
                .Where(l => l.OrgId == keeperId && l.DeletedAt == null)
                .Select(l => l.Slug)
                .ToListAsync(cancellationToken))
            .ToHashSet(StringComparer.Ordinal);

        foreach (var listing in duplicateListings)
        {
            var slug = listing.Slug;
            if (listing.DeletedAt is null)
            {
                slug = SupplierServiceListingRules.NextFreeSlug(slug, taken);
                taken.Add(slug);
            }

            var id = listing.Id;
            await db.SupplierServiceListings
                .Where(l => l.OrgId == duplicateId && l.Id == id)
                .ExecuteUpdateAsync(
                    set => set
                        .SetProperty(l => l.OrgId, keeperId)
                        .SetProperty(l => l.Slug, slug)
                        .SetProperty(l => l.UpdatedAt, now),
                    cancellationToken);
        }

        return duplicateListings.Count;
    }

    private async Task<int> MoveShowcaseContentAsync(Guid keeperId, Guid duplicateId, DateTime now, CancellationToken cancellationToken)
    {
        await PostgresAdvisoryLocks.BeginLockedTransactionAsync(
            db,
            cancellationToken,
            (PostgresAdvisoryLocks.Scope.SupplierShowcaseContent, keeperId.ToString("N")),
            (PostgresAdvisoryLocks.Scope.SupplierShowcaseContent, duplicateId.ToString("N")));

        if (!await db.SupplierShowcaseContents.Where(c => c.OrgId == duplicateId).AnyAsync(cancellationToken)
            || await db.SupplierShowcaseContents.Where(c => c.OrgId == keeperId).AnyAsync(cancellationToken))
        {
            return 0;
        }

        return await db.Database.ExecuteSqlAsync(
            $"UPDATE \"SupplierShowcaseContents\" SET \"OrgId\" = {keeperId}, \"UpdatedAt\" = {now} WHERE \"OrgId\" = {duplicateId}",
            cancellationToken);
    }

    /// <summary>
    /// Moves the agenda of the duplicate (SP-03) to the keeper and returns the rows moved. The keeper is the profile in use:
    /// <list type="bullet">
    /// <item>its weekly hours stay as they are; the duplicate's hours move only when the keeper has none (two weeks are
    /// never mixed band by band), and then the keeper's <c>HoursConfiguredAt</c> follows;</item>
    /// <item>its settings (the rules) stay; the duplicate's settings row moves only when the keeper has none;</item>
    /// <item>the duplicate's time off, blocks, extra openings and calendar engagements all move: dropping a closure would
    /// offer a slot the supplier had closed. The one exception is an engagement of the calendar feed that the keeper has too
    /// (same UID and start, <see cref="WindowsThatMove"/>): the keeper's row stays.</item>
    /// </list>
    /// What did not move goes with the duplicate profile (cascade).
    /// </summary>
    private async Task<int> MoveAgendaAsync(Guid keeperId, Guid duplicateId, DateTime now, CancellationToken cancellationToken)
    {
        await PostgresAdvisoryLocks.BeginLockedTransactionAsync(
            db,
            cancellationToken,
            CalendarSyncService.AvailabilityLock(keeperId),
            CalendarSyncService.AvailabilityLock(duplicateId));

        var moved = 0;

        var hoursMoved = 0;
        if (!await db.SupplierWorkingHours.Where(h => h.OrgId == keeperId).AnyAsync(cancellationToken))
        {
            hoursMoved = await db.SupplierWorkingHours
                .Where(h => h.OrgId == duplicateId)
                .ExecuteUpdateAsync(set => set.SetProperty(h => h.OrgId, keeperId), cancellationToken);
            moved += hoursMoved;
        }

        moved += await db.SupplierTimeOff
            .Where(t => t.OrgId == duplicateId)
            .ExecuteUpdateAsync(set => set.SetProperty(t => t.OrgId, keeperId), cancellationToken);
        moved += await WindowsThatMove(db, keeperId, duplicateId)
            .ExecuteUpdateAsync(set => set.SetProperty(w => w.OrgId, keeperId), cancellationToken);

        var duplicateSettings = await db.SupplierSettings
            .AsNoTracking()
            .Where(s => s.OrgId == duplicateId)
            .Select(s => new { s.HoursConfiguredAt })
            .FirstOrDefaultAsync(cancellationToken);
        if (duplicateSettings is not null)
        {
            var keeperSettings = await db.SupplierSettings
                .Where(s => s.OrgId == keeperId)
                .FirstOrDefaultAsync(cancellationToken);
            if (keeperSettings is null)
            {
                moved += await db.Database.ExecuteSqlAsync(
                    $"UPDATE \"SupplierSettings\" SET \"OrgId\" = {keeperId}, \"UpdatedAt\" = {now} WHERE \"OrgId\" = {duplicateId}",
                    cancellationToken);
            }
            else if (hoursMoved > 0 && keeperSettings.HoursConfiguredAt is null)
            {
                keeperSettings.HoursConfiguredAt = duplicateSettings.HoursConfiguredAt ?? now;
                keeperSettings.UpdatedAt = now;
                await db.SaveChangesAsync(cancellationToken);
            }
        }

        return moved;
    }

    /// <summary>
    /// The windows of the duplicate that move to the keeper (SP-03, SP-05): all of them, except the engagements of the calendar
    /// feed (<c>ExternalUid</c>) the keeper already has with the same UID and start. Those are unique per supplier (index
    /// <c>UIX_SupplierBusyWindows_OrgId_ExternalUid_StartUtc</c>, the iCal sync finds its rows by them), so moving the copy
    /// would break the merge; both profiles read the same calendar, the keeper's row stays and the duplicate's goes with the
    /// profile. The keeper's own sync rewrites its engagements at the next run, whatever the duplicate had.
    /// </summary>
    /// <remarks>Both orgs are explicit (the table is not tenant-filtered), as in every query of the agenda.</remarks>
    internal static IQueryable<SupplierBusyWindow> WindowsThatMove(AppDbContext db, Guid keeperId, Guid duplicateId) =>
        db.SupplierBusyWindows.Where(w => w.OrgId == duplicateId
                                          && (w.ExternalUid == null
                                              || !db.SupplierBusyWindows
                                                  .Where(k => k.OrgId == keeperId
                                                              && k.ExternalUid == w.ExternalUid
                                                              && k.StartUtc == w.StartUtc)
                                                  .Any()));

    private async Task<(int OrgMembers, int Devices, bool Deleted)> RemoveDuplicateOrgAsync(
        IDbContextTransaction transaction,
        Guid keeperId,
        Guid duplicateId,
        DateTime now,
        CancellationToken cancellationToken)
    {
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
                    set => set.SetProperty(u => u.OrgId, (Guid?)null).SetProperty(u => u.UpdatedAt, now),
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
            await transaction.RollbackToSavepointAsync(savepoint, cancellationToken);
            return (0, 0, false);
        }
    }

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
