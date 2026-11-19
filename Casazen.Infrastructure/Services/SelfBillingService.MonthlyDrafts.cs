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
/// <item><b>The run is the same as a click.</b> <see cref="RunMonthlyDraftsAsync"/> makes the drafts of the month with <see cref="CreateDraftsForPeriodAsync"/> (the same plan, the same lock, the same
/// refusals) and then raises the item <see cref="ActivityType.SelfBillingDraftsToReview"/> for the organization and the month. It never issues: the issue is a person's action, behind
/// <c>agency.invoice.manage</c> and <c>Features:FiscalDocumentIssuance</c>, and it checks the amounts the person confirms.</item>
/// <item><b>Once for each organization and month.</b> A month that has a TD17 in any state is already made: the run leaves it alone (so a retry, a second run or a trigger by hand makes no
/// second draft, and a draft a person discarded stays discarded) and only makes sure that the item stands while a draft is open. The item is one for each organization and month while it is
/// open (<see cref="IInternalActivityService.CreateAsync"/> returns the open one), and closes when no draft of the month is left.</item>
/// <item><b>The item follows the drafts, whoever changes them.</b> The generation, the issue and the discard of a draft close the item when the last open draft of the month is gone
/// (<see cref="TrySyncDraftsTodoAsync"/>: best effort after the change is saved, because the item is only a reminder), and only the monthly run raises it. Both read the count under the lock of
/// the generation (<see cref="PostgresAdvisoryLocks.Scope.SelfBilling"/>), so a run and a person's click never decide on a count the other is changing.</item>
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
            .Where(e => e.EntryType == OwnerLedgerEntryType.ChannelFee && e.PeriodYear == year && e.PeriodMonth == month)
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
        // A month that has a TD17 in any state was made already (by an earlier run, by a person's click, or all of it discarded): it is not made again.
        var alreadyMade = await db.FiscalDocuments
            .AsNoTracking()
            .AnyAsync(
                d => d.OrgId == orgId && d.Purpose == FiscalDocumentPurpose.SelfBilling && d.PeriodYear == year && d.PeriodMonth == month && d.Kind != DocumentKind.CreditNote,
                ct);

        var created = 0;
        var updated = 0;
        if (!alreadyMade)
        {
            var generation = await CreateDraftsForPeriodAsync(orgId, year, month, null, ct);
            created = generation.Created;
            updated = generation.Updated;
        }

        var open = await SyncDraftsTodoAsync(orgId, year, month, raise: true, ct);
        logger.LogInformation(
            "Monthly self-billed drafts of org {OrgId} for {Year}-{Month}: {Status}, {Created} made, {Updated} made again, {Open} drafts open",
            orgId,
            year,
            month,
            alreadyMade ? "already made" : "made now",
            created,
            updated,
            open);
        return new SelfBillingMonthlyRun(year, month, alreadyMade, created, updated, open);
    }

    /// <summary>
    /// Keeps the item <see cref="ActivityType.SelfBillingDraftsToReview"/> of the month in step with the drafts of the month, under the lock of the generation of the organization: a draft is open
    /// (<see cref="FiscalDocumentStatus.Draft"/>) and <paramref name="raise"/> is set, the item is raised (once: while it is open the same one is returned); no draft is open, the item is
    /// closed by the system. Returns the number of open drafts. Nothing without the service of the «Da fare».
    /// </summary>
    private async Task<int> SyncDraftsTodoAsync(Guid orgId, int year, int month, bool raise, CancellationToken ct)
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
                await RaiseDraftsTodoAsync(activities, orgId, year, month, entityType, ct);
            }
            else if (open == 0 && await activities.FindOpenAsync(orgId, nameof(ActivityAction.ReviewSelfBilling), entityType, orgId, ct) is { } item)
            {
                await activities.CloseAsync(item.Id, InternalActivity.ClosedBySystem, ct);
                logger.LogInformation("«Da fare» of the self-billed drafts of {Year}-{Month} of org {OrgId} closed: no draft is left open", year, month, orgId);
            }
        }

        if (transaction is not null)
            await transaction.CommitAsync(ct);

        return open;
    }

    /// <summary>
    /// The item of the month raised: about the month (the kind of entity names it, the id is the organization's), organization-level, medium urgency, due the day the TD17 are issued by
    /// (<see cref="SelfBillingOptions.IssueByDay"/>, the end of that day in Europe/Rome), with the words of the organization's language and no data of the TD17.
    /// </summary>
    private async Task RaiseDraftsTodoAsync(IInternalActivityService todos, Guid orgId, int year, int month, string entityType, CancellationToken ct)
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
                Reason = await BookingTodoItems.ReasonAsync(db, orgId, SelfBillingTodos.DraftsReasonKey, ct),
            },
            ct);
    }

    /// <summary>
    /// <see cref="SyncDraftsTodoAsync"/> that only closes, after a change of the drafts was saved (a generation, an issue, a discard): the item is a reminder, so a failure of it is logged
    /// and never undoes or fails what the person just did; the next change of the month, or the next run, puts it right.
    /// </summary>
    private async Task TrySyncDraftsTodoAsync(Guid orgId, int year, int month, CancellationToken ct)
    {
        if (activities is null)
            return;

        try
        {
            await SyncDraftsTodoAsync(orgId, year, month, raise: false, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError("«Da fare» of the self-billed drafts of {Year}-{Month} of org {OrgId} could not be updated ({Error})", year, month, orgId, ex.GetType().Name);
        }
    }
}
