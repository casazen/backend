using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;
using Casazen.Core.Utilities;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Events;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using DocumentKind = Casazen.Core.Entities.Enums.FiscalDocumentKind;

namespace Casazen.Infrastructure.Services;

/// <summary>
/// The monthly run of the self-billed invoices (TD17) and the «Da fare» of their drafts (FIX/BE-TD17-ALERTS).
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><b>The run is the same as a click, for what is missing.</b> <see cref="RunMonthlyDraftsAsync"/> makes the drafts of the month with the plan of <see cref="CreateDraftsForPeriodAsync"/> (the
/// same plan, the same lock, the same refusals) and then raises the item <see cref="ActivityType.SelfBillingDraftsToReview"/> for the organization and the month. It never issues: the issue is a
/// person's action, behind <c>agency.invoice.manage</c> and <c>Features:FiscalDocumentIssuance</c>, and it checks the amounts the person confirms.</item>
/// <item><b>No second draft for who has one.</b> A holder and portal that has a TD17 of the month in any state is left alone by the run (so a retry, a second run or a trigger by hand makes no
/// second draft for it, and a draft a person discarded stays discarded): only the holders that have none get a draft. The item is one for each organization and month while it is
/// open (<see cref="IInternalActivityService.CreateAsync"/> returns the open one), and closes when no draft of the month is left.</item>
/// <item><b>A month with a holder refused is not a month done.</b> The holders that need a TD17 and could not get one (the issuer profile is not complete: <see cref="SelfBillingGeneration.Failed"/>) are
/// counted and their codes given in the answer, logged as a warning (codes and counts, never a name, a VAT number or a field), said in the words of the item while a draft is open
/// (<see cref="SelfBillingTodos.DraftsRefusedReasonKey"/>, the same item changed in place, never closed or raised again for it), and tried again by the next run: the holders that already have a
/// draft are skipped, so the retry makes no second one.</item>
/// <item><b>The item follows the drafts, whoever changes them.</b> The generation, the issue and the discard of a draft close the item when the last open draft of the month is gone
/// (<see cref="TrySyncDraftsTodoAsync"/>: best effort after the change is saved, because the item is only a reminder), and only the monthly run raises it. Both read the count under the lock of
/// the generation (<see cref="PostgresAdvisoryLocks.Scope.SelfBilling"/>), so a run and a person's click never decide on a count the other is changing. A close that failed is put right by the
/// next monthly run, which closes the items of every month of the organization that has no draft left open (<see cref="CloseStaleDraftsTodosAsync"/>).</item>
/// <item><b>Tenant.</b> Every read names the organization; the listing of the organizations to run reads the ledger of every tenant and nothing else of them.</item>
/// </list>
/// </remarks>
public sealed partial class SelfBillingService
{
    public async Task<IReadOnlyList<Guid>> OrgsWithCommissionsAsync(int year, int month, CancellationToken ct = default)
    {
        // The organizations that have commissions in the ledger of the month, of every tenant (the caller is a job: no tenant is entered while it lists).
        var withFees = await db.OwnerLedgerEntries
            .IgnoreQueryFilters([AppDbContext.TenantQueryFilter])
            .AsNoTracking()
            .Where(e => (e.EntryType == OwnerLedgerEntryType.ChannelFee || e.EntryType == OwnerLedgerEntryType.ChannelFeeBorneByAgency) && e.PeriodYear == year && e.PeriodMonth == month)
            .Select(e => e.OrgId)
            .Distinct()
            .ToListAsync(ct);
        if (withFees.Count == 0)
            return [];

        // Only the ones that have the feature: the agency plan (the policy of the endpoints), not downgraded to reads only, and active.
        return await db.Orgs
            .AsNoTracking()
            .Where(o => withFees.Contains(o.Id) && o.IsActive && o.PlanTier == PlanTier.Agency && !o.IsAgencyTierDowngraded)
            .OrderBy(o => o.Id)
            .Select(o => o.Id)
            .ToListAsync(ct);
    }

    public async Task<SelfBillingMonthlyRun> RunMonthlyDraftsAsync(Guid orgId, int year, int month, CancellationToken ct = default)
    {
        // A month that has a TD17 in any state was made at least in part (by an earlier run, by a person's click, or all of it discarded): said in the answer, and the holders that have one are left alone.
        var alreadyMade = await db.FiscalDocuments
            .AsNoTracking()
            .AnyAsync(
                d => d.OrgId == orgId && d.Purpose == FiscalDocumentPurpose.SelfBilling && d.PeriodYear == year && d.PeriodMonth == month && d.Kind != DocumentKind.CreditNote,
                ct);

        // Only what is missing is made: a holder that has a TD17 of the month in any state gets no second draft (a discarded one stays discarded), a holder that was refused by an earlier run is tried again.
        var generation = await GenerateDraftsAsync(orgId, year, month, null, onlyMissing: true, ct);
        var refused = generation.Failed.Count;
        var refusedCodes = generation.Failed.Select(f => f.Code).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();

        var open = await SyncDraftsTodoAsync(orgId, year, month, raise: true, refused, ct);

        // The items of the other months that have no draft left open are closed too: the close that follows a change is best effort (TrySyncDraftsTodoAsync), this is what puts it right.
        await CloseStaleDraftsTodosAsync(orgId, year, month, ct);

        // A month in which somebody was refused is not a month done: the codes and the count (never a name, a VAT number or a field) are in the log and in the answer, and the next run tries them again.
        if (refused > 0)
        {
            logger.LogWarning(
                "Monthly self-billed drafts of org {OrgId} for {Year}-{Month}: {Refused} holders need a TD17 and could not get one ({Codes}); they are tried again by the next run",
                orgId,
                year,
                month,
                refused,
                string.Join(',', refusedCodes));
        }

        logger.LogInformation(
            "Monthly self-billed drafts of org {OrgId} for {Year}-{Month}: {Status}, {Created} made, {Open} drafts open, {Refused} holders refused",
            orgId,
            year,
            month,
            alreadyMade ? "month already started" : "month started now",
            generation.Created,
            open,
            refused);
        return new SelfBillingMonthlyRun(year, month, alreadyMade, generation.Created, generation.Updated, open, refused, refusedCodes);
    }

    /// <summary>
    /// Keeps the item <see cref="ActivityType.SelfBillingDraftsToReview"/> of the month in step with the drafts of the month, under the lock of the generation of the organization: a draft is open
    /// (<see cref="FiscalDocumentStatus.Draft"/>) and <paramref name="raise"/> is set, the item is raised (once: while it is open the same one is returned); no draft is open, the item is
    /// closed by the system. <paramref name="refused"/> is the number of holders refused when the caller knows it (<c>null</c> when it does not): while a draft is open the words of the item
    /// follow it, in place (the item is neither closed nor raised again for it). Returns the number of open drafts. Nothing without the service of the «Da fare».
    /// </summary>
    private async Task<int> SyncDraftsTodoAsync(Guid orgId, int year, int month, bool raise, int? refused, CancellationToken ct)
    {
        await using var transaction = await PostgresAdvisoryLocks.BeginLockedTransactionAsync(db, ct, (PostgresAdvisoryLocks.Scope.SelfBilling, orgId.ToString("N")));

        var open = await db.FiscalDocuments
            .AsNoTracking()
            .CountAsync(
                d => d.OrgId == orgId && d.Purpose == FiscalDocumentPurpose.SelfBilling && d.PeriodYear == year && d.PeriodMonth == month
                     && d.Status == FiscalDocumentStatus.Draft && d.Kind != DocumentKind.CreditNote,
                ct);
        if (activities is not null)
        {
            var entityType = SelfBillingTodos.PeriodEntityType(year, month);
            if (open > 0 && raise)
            {
                await RaiseDraftsTodoAsync(activities, orgId, year, month, entityType, refused > 0, ct);
            }
            else if (open == 0 && await activities.FindOpenAsync(orgId, nameof(ActivityAction.ReviewSelfBilling), entityType, orgId, ct) is { } item)
            {
                await activities.CloseAsync(item.Id, InternalActivity.ClosedBySystem, ct);
                logger.LogInformation("«Da fare» of the self-billed drafts of {Year}-{Month} of org {OrgId} closed: no draft is left open", year, month, orgId);
            }

            if (open > 0 && refused is { } known)
                await SetDraftsTodoWordsAsync(orgId, entityType, known > 0, ct);
        }

        if (transaction is not null)
            await transaction.CommitAsync(ct);

        return open;
    }

    /// <summary>
    /// The item of the month raised: about the month (the kind of entity names it, the id is the organization's), organization-level, medium urgency, due the day the TD17 are issued by
    /// (<see cref="SelfBillingOptions.IssueByDay"/>, the end of that day in Europe/Rome), with the words of the organization's language (the ones that tell of the holders refused when there
    /// are some) and no data of the TD17.
    /// </summary>
    private async Task RaiseDraftsTodoAsync(IInternalActivityService todos, Guid orgId, int year, int month, string entityType, bool refused, CancellationToken ct)
    {
        var due = SelfBillingRules.DueDate(year, month, options.Value.EffectiveIssueByDay);
        await todos.CreateAsync(
            new CreateTodoRequest
            {
                Type = ActivityType.SelfBillingDraftsToReview,
                Action = ActivityAction.ReviewSelfBilling,
                EntityType = entityType,
                EntityId = orgId,
                Urgency = ActivityUrgency.Medium,
                OrgId = orgId,
                Deadline = RomeCalendar.StartOfDayUtc(due.AddDays(1)),
                Reason = await BookingTodoItems.ReasonAsync(db, orgId, WordsOf(refused), ct),
            },
            ct);
    }

    /// <summary>The key of the words of the item of the drafts: the ones that tell of the holders refused when there are some.</summary>
    private static string WordsOf(bool refused) => refused ? SelfBillingTodos.DraftsRefusedReasonKey : SelfBillingTodos.DraftsReasonKey;

    /// <summary>
    /// The words of the open item of the month follow the holders refused, in place: the same item (never closed, never raised again) says that somebody was refused while somebody is, and goes back
    /// to its plain words when every holder has its draft. Nothing for a month that has no open item; the lock of the generation is held by the caller.
    /// </summary>
    private async Task SetDraftsTodoWordsAsync(Guid orgId, string entityType, bool refused, CancellationToken ct)
    {
        var item = await db.InternalActivities.FirstOrDefaultAsync(
            a => a.OrgId == orgId && a.ActivityType == nameof(ActivityType.SelfBillingDraftsToReview) && a.EntityType == entityType && a.EntityId == orgId && a.Action != null && a.CompletedAt == null,
            ct);
        if (item is null)
            return;

        var reason = await BookingTodoItems.ReasonAsync(db, orgId, WordsOf(refused), ct);
        if (string.Equals(item.Reason, reason, StringComparison.Ordinal))
            return;

        item.Reason = reason;
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Closes the items <see cref="ActivityType.SelfBillingDraftsToReview"/> of the organization that have no draft left open, <b>of any month but</b> the one the run just did (that one was kept in step
    /// already): the close that follows an issue, a discard or a generation is best effort and may have failed, and nothing else looks at a month once all its drafts are gone. A month that still has an
    /// open draft keeps its item, and no other organization is read: every read names the organization.
    /// </summary>
    private async Task CloseStaleDraftsTodosAsync(Guid orgId, int exceptYear, int exceptMonth, CancellationToken ct)
    {
        if (activities is null)
            return;

        var entityTypes = await db.InternalActivities
            .AsNoTracking()
            .Where(a => a.OrgId == orgId && a.ActivityType == nameof(ActivityType.SelfBillingDraftsToReview) && a.Action != null && a.CompletedAt == null)
            .Select(a => a.EntityType)
            .ToListAsync(ct);
        foreach (var entityType in entityTypes)
        {
            if (SelfBillingTodos.TryParsePeriodEntityType(entityType, out var year, out var month) && (year != exceptYear || month != exceptMonth))
                await SyncDraftsTodoAsync(orgId, year, month, raise: false, refused: null, ct);
        }
    }

    /// <summary>
    /// <see cref="SyncDraftsTodoAsync"/> that only closes (and says who was refused), after a change of the drafts was saved (a generation, an issue, a discard): the item is a reminder, so a failure of
    /// it is logged and never undoes or fails what the person just did; the next change of the month puts it right, and so does the next monthly run (it closes every item of the organization that has
    /// no draft left open, whatever its month).
    /// </summary>
    private async Task TrySyncDraftsTodoAsync(Guid orgId, int year, int month, int? refused, CancellationToken ct)
    {
        if (activities is null)
            return;

        try
        {
            await SyncDraftsTodoAsync(orgId, year, month, raise: false, refused, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError("«Da fare» of the self-billed drafts of {Year}-{Month} of org {OrgId} could not be updated ({Error})", year, month, orgId, ex.GetType().Name);
        }
    }
}
