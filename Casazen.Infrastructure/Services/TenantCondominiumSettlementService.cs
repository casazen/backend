using Casazen.Core.Authorization;
using Casazen.Core.Condominium;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Services;
using Casazen.Core.Utilities;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;
using System.Data;

namespace Casazen.Infrastructure.Services;

/// <summary>
/// Posts the tenant's consuntivo settlement onto the rent ledger. The consuntivo comes from
/// <see cref="ICondominiumAccountingService"/>; this class does not query the condominium tables.
/// Issued and paid installments keep their rent and advance amounts. Sending the result to the tenant is left to a person.
/// </summary>
public sealed class TenantCondominiumSettlementService(
    AppDbContext db,
    ICondominiumAccountingService condominiums,
    ILogger<TenantCondominiumSettlementService> logger,
    TimeProvider? timeProvider = null) : ITenantCondominiumSettlementService
{
    private const string SettlementDescription = "Conguaglio condominiale";
    private const string ReversalDescription = "Storno conguaglio";

    private DateTime UtcNow => timeProvider?.GetUtcNow().UtcDateTime ?? DateTime.UtcNow;

    public async Task<TenantSettlementView> CalculateAsync(
        HostScope scope,
        Guid leaseId,
        CalculateTenantSettlementCommand command,
        string userId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(command);
        RequireUser(userId);
        var disposition = command.Disposition ?? TenantSettlementDisposition.NextInstallment;
        if (!Enum.IsDefined(disposition) || command.Rule is { } rule && !Enum.IsDefined(rule))
            throw Invalid();

        var lease = await RequireLeaseAsync(scope, leaseId, cancellationToken);
        var final = await condominiums.GetConfirmedFinalForPropertyAsync(scope, lease.PropertyId, command.PeriodId, cancellationToken);
        var terms = await db.LeaseCondominiumTerms
            .AsNoTracking()
            .SingleOrDefaultAsync(item => item.LeaseContractId == leaseId && item.OrgId == scope.OrgId, cancellationToken);
        var allocationRule = command.Rule ?? TenantSettlementMath.RuleFromContract(terms?.ProrationRule);
        var explicitAmounts = ExplicitAmounts(command.ExplicitLines, final);
        var occupancy = OccupancyOf(lease, terms);
        var allocations = TenantSettlementMath.Allocate(
            final.Lines.Select(line => new TenantSettlementMath.LineInput(
                line.LineId, line.RecoverableAmount, line.CompetenceStart, line.CompetenceEnd, line.ChargesTenant)).ToList(),
            occupancy,
            allocationRule,
            explicitAmounts);
        var recoverable = CondominiumMoney.Round(allocations.Where(item => item.Charged).Sum(item => item.AllocatedAmount));
        var advances = await AdvancesAsync(scope, leaseId, final.PeriodStart, final.PeriodEnd, cancellationToken);
        var replacing = await db.TenantCondominiumSettlements
            .AsNoTracking()
            .Where(item => item.OrgId == scope.OrgId
                && item.LeaseContractId == leaseId
                && item.CondominiumPeriodId == final.PeriodId
                && item.Status == TenantSettlementStatus.Posted
                && item.StatementId != final.StatementId)
            .Select(item => item.Id)
            .ToListAsync(cancellationToken);
        var priors = await db.TenantCondominiumSettlements
            .Where(item => item.OrgId == scope.OrgId
                && item.LeaseContractId == leaseId
                && item.Disposition == TenantSettlementDisposition.CarryForward
                && item.PeriodEnd < final.PeriodStart
                && (item.Status == TenantSettlementStatus.Posted
                    || (item.Status == TenantSettlementStatus.Settled
                        && item.AbsorbedBySettlementId != null
                        && replacing.Contains(item.AbsorbedBySettlementId.Value))))
            .ToListAsync(cancellationToken);
        var priorAmount = CondominiumMoney.Round(priors.Sum(item => item.SettlementAmount));
        var advanceAmount = CondominiumMoney.Round(advances.Sum(item => item.Amount));
        var totals = TenantSettlementMath.Settle(recoverable, priorAmount, advanceAmount);
        if (disposition == TenantSettlementDisposition.Refund && totals.Direction == TenantSettlementDirection.Debit)
            throw new DomainRuleException(TenantSettlementErrorCodes.RefundNotCredit, "TenantSettlementRefundNotCredit");

        var snapshot = new TenantSettlementMath.Snapshot
        {
            StatementId = final.StatementId,
            Revision = final.RevisionNumber,
            Recoverable = recoverable,
            Prior = priorAmount,
            Advances = advanceAmount,
            Amount = totals.Amount,
            Direction = totals.Direction,
            Disposition = disposition,
            Rule = allocationRule,
            Lines = allocations.Select(item => new TenantSettlementMath.SnapshotLine(
                item.LineId,
                item.Rule,
                item.LineDays,
                item.OccupiedDays,
                item.BaseAmount,
                item.AllocatedAmount,
                item.Charged,
                final.Lines.FirstOrDefault(line => line.LineId == item.LineId)?.Description)).ToList(),
            AdvancesDetail = advances.Select(item => new TenantSettlementMath.SnapshotAdvance(item.ComponentId, item.EntryId, item.Amount)).ToList(),
            PriorSettlementIds = priors.Select(item => item.Id).ToList(),
        };
        _ = TenantSettlementMath.Replay(TenantSettlementMath.Serialize(snapshot));

        var settlement = await db.TenantCondominiumSettlements
            .Include(item => item.Allocations)
            .SingleOrDefaultAsync(
                item => item.OrgId == scope.OrgId && item.LeaseContractId == leaseId && item.StatementId == final.StatementId,
                cancellationToken);
        if (settlement is { Status: TenantSettlementStatus.Posted or TenantSettlementStatus.Settled })
            return await ToViewAsync(settlement, cancellationToken);

        var now = UtcNow;
        if (settlement is null)
        {
            settlement = new TenantCondominiumSettlement
            {
                OrgId = scope.OrgId,
                LeaseContractId = leaseId,
                CreatedAt = now,
            };
            db.TenantCondominiumSettlements.Add(settlement);
        }
        else
        {
            db.TenantExpenseAllocations.RemoveRange(settlement.Allocations);
            settlement.Allocations.Clear();
            settlement.ConfirmedAt = null;
            settlement.ConfirmedByUserId = null;
            settlement.Status = TenantSettlementStatus.Draft;
        }

        settlement.CondominiumPeriodId = final.PeriodId;
        settlement.StatementId = final.StatementId;
        settlement.StatementRevision = final.RevisionNumber;
        settlement.PeriodStart = final.PeriodStart;
        settlement.PeriodEnd = final.PeriodEnd;
        settlement.RecoverableFinalAmount = recoverable;
        settlement.AdvancesAccountedAmount = advanceAmount;
        settlement.PriorTenantBalanceAmount = priorAmount;
        settlement.SettlementAmount = totals.Amount;
        settlement.Direction = totals.Direction;
        settlement.Disposition = disposition;
        settlement.AllocationRule = allocationRule;
        settlement.CalculationSnapshotJson = TenantSettlementMath.Serialize(snapshot);
        settlement.UpdatedAt = now;
        foreach (var allocation in allocations)
        {
            settlement.Allocations.Add(new TenantExpenseAllocation
            {
                OrgId = scope.OrgId,
                LeaseContractId = leaseId,
                CondominiumExpenseLineId = allocation.LineId,
                Rule = allocation.Rule,
                OccupancyStart = allocation.OccupancyStart,
                OccupancyEnd = allocation.OccupancyEnd,
                BaseAmount = allocation.BaseAmount,
                AllocatedAmount = allocation.AllocatedAmount,
                CalculationDetailsJson = TenantSettlementMath.Serialize(snapshot),
                Status = allocation.Charged ? TenantAllocationStatus.Proposed : TenantAllocationStatus.Excluded,
            });
        }

        await db.SaveChangesAsync(cancellationToken);
        logger.LogInformation(
            "Tenant settlement {SettlementId} calculated for lease {LeaseId} ({Direction})",
            settlement.Id, leaseId, settlement.Direction);
        return await ToViewAsync(settlement, cancellationToken);
    }

    public async Task<TenantSettlementView> ConfirmAsync(
        HostScope scope, Guid leaseId, Guid settlementId, string userId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        RequireUser(userId);
        await RequireLeaseAsync(scope, leaseId, cancellationToken);
        var settlement = await RequireSettlementAsync(scope, leaseId, settlementId, cancellationToken);
        if (settlement.Status is TenantSettlementStatus.Confirmed or TenantSettlementStatus.Posted or TenantSettlementStatus.Settled)
            return await ToViewAsync(settlement, cancellationToken);
        if (settlement.Status != TenantSettlementStatus.Draft)
            throw Invalid();

        var now = UtcNow;
        settlement.Status = TenantSettlementStatus.Confirmed;
        settlement.ConfirmedAt = now;
        settlement.ConfirmedByUserId = userId;
        settlement.UpdatedAt = now;
        foreach (var allocation in settlement.Allocations.Where(item => item.Status == TenantAllocationStatus.Proposed))
        {
            allocation.Status = TenantAllocationStatus.Confirmed;
            allocation.ConfirmedAt = now;
            allocation.ConfirmedByUserId = userId;
        }

        await db.SaveChangesAsync(cancellationToken);
        logger.LogInformation("Tenant settlement {SettlementId} confirmed", settlement.Id);
        return await ToViewAsync(settlement, cancellationToken);
    }

    public async Task<TenantSettlementView> PostAsync(
        HostScope scope, Guid leaseId, Guid settlementId, string userId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        RequireUser(userId);
        await RequireLeaseAsync(scope, leaseId, cancellationToken);
        await using var transaction = await LockLeaseAsync(leaseId, cancellationToken);
        var settlement = await RequireSettlementAsync(scope, leaseId, settlementId, cancellationToken);
        if (settlement.Status == TenantSettlementStatus.Posted)
            return await ToViewAsync(settlement, cancellationToken);
        if (settlement.Status != TenantSettlementStatus.Confirmed)
            throw new DomainRuleException(TenantSettlementErrorCodes.NotConfirmed, "TenantSettlementNotConfirmed");

        var previous = await db.TenantCondominiumSettlements
            .Where(item => item.OrgId == scope.OrgId
                && item.LeaseContractId == leaseId
                && item.CondominiumPeriodId == settlement.CondominiumPeriodId
                && item.Status == TenantSettlementStatus.Posted
                && item.Id != settlement.Id)
            .ToListAsync(cancellationToken);
        foreach (var old in previous)
            await ReverseAsync(old, cancellationToken);

        await ApplyDispositionAsync(settlement, cancellationToken);
        await AbsorbPriorsAsync(settlement, cancellationToken);
        var now = UtcNow;
        settlement.Status = TenantSettlementStatus.Posted;
        settlement.PostedAt = now;
        settlement.PostedByUserId = userId;
        settlement.SentToTenantAt = null;
        settlement.UpdatedAt = now;
        await db.SaveChangesAsync(cancellationToken);
        if (transaction is not null)
            await transaction.CommitAsync(cancellationToken);
        logger.LogInformation("Tenant settlement {SettlementId} posted ({Disposition})", settlement.Id, settlement.Disposition);
        return await ToViewAsync(settlement, cancellationToken);
    }

    public async Task<IReadOnlyList<TenantSettlementView>> ReplacePostedForPeriodAsync(
        HostScope scope, Guid periodId, string userId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        RequireUser(userId);
        var posted = await db.TenantCondominiumSettlements
            .AsNoTracking()
            .Where(item => item.OrgId == scope.OrgId
                && item.CondominiumPeriodId == periodId
                && item.Status == TenantSettlementStatus.Posted)
            .Select(item => new
            {
                item.Id,
                item.LeaseContractId,
                item.StatementId,
                item.Disposition,
                item.AllocationRule,
                item.CalculationSnapshotJson,
            })
            .ToListAsync(cancellationToken);
        var views = new List<TenantSettlementView>(posted.Count);
        foreach (var row in posted)
        {
            var lease = await RequireLeaseAsync(scope, row.LeaseContractId, cancellationToken);
            TenantFinalStatementView final;
            try
            {
                final = await condominiums.GetConfirmedFinalForPropertyAsync(scope, lease.PropertyId, periodId, cancellationToken);
            }
            catch (DomainException ex) when (ex.Code == CondominiumErrorCodes.FinalNotConfirmed)
            {
                continue;
            }

            if (final.StatementId == row.StatementId)
            {
                var current = await RequireSettlementAsync(scope, row.LeaseContractId, row.Id, cancellationToken);
                views.Add(await ToViewAsync(current, cancellationToken));
                continue;
            }

            var calculated = await CalculateAsync(
                scope,
                row.LeaseContractId,
                new CalculateTenantSettlementCommand(
                    periodId,
                    row.Disposition,
                    row.AllocationRule,
                    ExplicitLinesFromSnapshot(row.CalculationSnapshotJson, final.Lines)),
                userId,
                cancellationToken);
            if (calculated.Status == TenantSettlementStatus.Draft)
                calculated = await ConfirmAsync(scope, row.LeaseContractId, calculated.Id, userId, cancellationToken);
            if (calculated.Status == TenantSettlementStatus.Confirmed)
                calculated = await PostAsync(scope, row.LeaseContractId, calculated.Id, userId, cancellationToken);
            views.Add(calculated);
        }

        return views;
    }

    public async Task<FinalConfirmation> ConfirmFinalAndReplaceAsync(
        HostScope scope,
        Guid condominiumId,
        Guid periodId,
        ConfirmFinalCommand command,
        string userId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(command);
        await using var transaction = await BeginUnitOfWorkAsync(cancellationToken);
        try
        {
            var confirmation = await condominiums.ConfirmFinalAsync(
                scope, condominiumId, periodId, command, userId, cancellationToken);
            await ReplacePostedForPeriodAsync(scope, periodId, userId, cancellationToken);
            if (transaction is not null)
                await transaction.CommitAsync(cancellationToken);
            return confirmation;
        }
        catch
        {
            if (transaction is not null)
                await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    private async Task ApplyDispositionAsync(TenantCondominiumSettlement settlement, CancellationToken cancellationToken)
    {
        if (settlement.SettlementAmount == 0m
            || settlement.Disposition is TenantSettlementDisposition.Refund or TenantSettlementDisposition.CarryForward)
            return;

        var entries = await db.RentLedgerEntries
            .Include(entry => entry.Components)
            .Where(entry => entry.LeaseContractId == settlement.LeaseContractId && entry.OrgId == settlement.OrgId)
            .OrderBy(entry => entry.PeriodStart)
            .ToListAsync(cancellationToken);
        if (entries.Any(entry => entry.Components.Any(component => component.TenantSettlementId == settlement.Id)))
            return;

        if (settlement.Disposition == TenantSettlementDisposition.SeparateInstallment)
        {
            var scheduleId = await RequireScheduleIdAsync(settlement, cancellationToken);
            AddSeparateInstallment(settlement, scheduleId, UsedDays(entries), settlement.SettlementAmount, settlement.Id, SettlementDescription);
            return;
        }

        var target = entries.FirstOrDefault(OpenForSettlement);
        if (target is null)
            throw new DomainRuleException(TenantSettlementErrorCodes.NoOpenInstallment, "TenantSettlementNoOpenInstallment");
        if (target.AmountDue + settlement.SettlementAmount < 0m)
            throw new DomainRuleException(TenantSettlementErrorCodes.CreditExceedsInstallment, "TenantSettlementCreditExceedsInstallment");

        AddComponent(target, settlement.SettlementAmount, settlement.PeriodStart, settlement.PeriodEnd, settlement.Id, null, SettlementDescription);
    }

    private async Task ReverseAsync(TenantCondominiumSettlement old, CancellationToken cancellationToken)
    {
        var component = await db.RentLedgerComponents
            .Include(item => item.RentLedgerEntry)
            .ThenInclude(entry => entry.Components)
            .SingleOrDefaultAsync(item => item.TenantSettlementId == old.Id, cancellationToken);
        if (component is not null)
        {
            var entry = component.RentLedgerEntry;
            if (IsSettlementOnly(entry) && !Frozen(entry))
            {
                entry.Status = RentLedgerStatus.Cancelled;
                entry.UpdatedAt = UtcNow;
            }
            else if (!Frozen(entry))
            {
                var rent = Amounts(entry, RentLedgerComponentKind.Rent);
                var advance = Amounts(entry, RentLedgerComponentKind.CondominiumAdvance);
                entry.Components.Remove(component);
                db.RentLedgerComponents.Remove(component);
                entry.AmountDue = entry.Components.Sum(item => item.Amount);
                entry.UpdatedAt = UtcNow;
                if (!rent.SequenceEqual(Amounts(entry, RentLedgerComponentKind.Rent))
                    || !advance.SequenceEqual(Amounts(entry, RentLedgerComponentKind.CondominiumAdvance)))
                {
                    throw new InvalidOperationException("Reversing a settlement changed a rent or advance amount.");
                }
            }
            else
            {
                var used = await db.RentLedgerEntries
                    .Where(item => item.LeaseContractId == old.LeaseContractId && item.OrgId == old.OrgId)
                    .Select(item => item.PeriodStart)
                    .ToListAsync(cancellationToken);
                AddSeparateInstallment(
                    old, entry.RentScheduleId, used.ToHashSet(), -component.Amount, null, ReversalDescription, old.Id);
            }
        }

        var absorbed = await db.TenantCondominiumSettlements
            .Where(item => item.AbsorbedBySettlementId == old.Id)
            .ToListAsync(cancellationToken);
        foreach (var prior in absorbed)
        {
            prior.Status = TenantSettlementStatus.Posted;
            prior.AbsorbedBySettlementId = null;
            prior.UpdatedAt = UtcNow;
        }

        old.Status = TenantSettlementStatus.Superseded;
        old.UpdatedAt = UtcNow;
    }

    private async Task AbsorbPriorsAsync(TenantCondominiumSettlement settlement, CancellationToken cancellationToken)
    {
        var snapshot = TenantSettlementMath.Replay(settlement.CalculationSnapshotJson);
        if (snapshot.PriorSettlementIds.Count == 0)
            return;

        var priors = await db.TenantCondominiumSettlements
            .Where(item => snapshot.PriorSettlementIds.Contains(item.Id) && item.OrgId == settlement.OrgId)
            .ToListAsync(cancellationToken);
        foreach (var prior in priors)
        {
            prior.Status = TenantSettlementStatus.Settled;
            prior.AbsorbedBySettlementId = settlement.Id;
            prior.UpdatedAt = UtcNow;
        }
    }

    private async Task<Guid> RequireScheduleIdAsync(TenantCondominiumSettlement settlement, CancellationToken cancellationToken)
    {
        var scheduleId = await db.RentSchedules
            .Where(schedule => schedule.LeaseContractId == settlement.LeaseContractId && schedule.OrgId == settlement.OrgId)
            .Select(schedule => (Guid?)schedule.Id)
            .FirstOrDefaultAsync(cancellationToken);
        if (scheduleId is null)
            throw new DomainRuleException(TenantSettlementErrorCodes.NoSchedule, "TenantSettlementNoSchedule");
        return scheduleId.Value;
    }

    private void AddSeparateInstallment(
        TenantCondominiumSettlement settlement,
        Guid scheduleId,
        HashSet<DateOnly> used,
        decimal amount,
        Guid? tenantSettlementId,
        string description,
        Guid? sourceId = null)
    {
        foreach (var local in db.RentLedgerEntries.Local)
            used.Add(local.PeriodStart);

        var day = settlement.PeriodEnd.AddDays(1);
        while (used.Contains(day))
            day = day.AddDays(1);
        used.Add(day);

        var now = UtcNow;
        var entry = new RentLedgerEntry
        {
            OrgId = settlement.OrgId,
            LeaseContractId = settlement.LeaseContractId,
            RentScheduleId = scheduleId,
            PeriodStart = day,
            PeriodEnd = day,
            DueDate = day,
            AmountDue = amount,
            Status = RentLedgerStatus.Scheduled,
            CreatedAt = now,
            UpdatedAt = now,
        };
        AddComponent(entry, amount, settlement.PeriodStart, settlement.PeriodEnd, tenantSettlementId, sourceId, description);
        db.RentLedgerEntries.Add(entry);
    }

    private void AddComponent(
        RentLedgerEntry entry,
        decimal amount,
        DateOnly competenceStart,
        DateOnly competenceEnd,
        Guid? tenantSettlementId,
        Guid? sourceId,
        string description)
    {
        var rent = Amounts(entry, RentLedgerComponentKind.Rent);
        var advance = Amounts(entry, RentLedgerComponentKind.CondominiumAdvance);
        var component = new RentLedgerComponent
        {
            Id = Guid.NewGuid(),
            OrgId = entry.OrgId,
            RentLedgerEntry = entry,
            Kind = RentLedgerComponentKind.CondominiumSettlement,
            Amount = amount,
            CompetenceStart = competenceStart,
            CompetenceEnd = competenceEnd,
            TenantSettlementId = tenantSettlementId,
            SourceId = sourceId,
            Description = description,
        };
        entry.Components.Add(component);
        entry.AmountDue = entry.Components.Sum(item => item.Amount);
        entry.UpdatedAt = UtcNow;
        if (db.Entry(entry).State != EntityState.Detached)
            db.Entry(component).State = EntityState.Added;
        if (!rent.SequenceEqual(Amounts(entry, RentLedgerComponentKind.Rent))
            || !advance.SequenceEqual(Amounts(entry, RentLedgerComponentKind.CondominiumAdvance)))
        {
            throw new InvalidOperationException("Posting a settlement changed a rent or advance amount.");
        }
    }

    private async Task<List<AdvanceRow>> AdvancesAsync(
        HostScope scope, Guid leaseId, DateOnly periodStart, DateOnly periodEnd, CancellationToken cancellationToken) =>
        await db.RentLedgerComponents
            .AsNoTracking()
            .Where(component => component.OrgId == scope.OrgId
                && component.Kind == RentLedgerComponentKind.CondominiumAdvance
                && component.RentLedgerEntry.LeaseContractId == leaseId
                && component.RentLedgerEntry.Status != RentLedgerStatus.Cancelled
                && component.CompetenceStart <= periodEnd
                && component.CompetenceEnd >= periodStart)
            .Select(component => new AdvanceRow(component.Id, component.RentLedgerEntryId, component.Amount))
            .ToListAsync(cancellationToken);

    private async Task<LeaseRow> RequireLeaseAsync(HostScope scope, Guid leaseId, CancellationToken cancellationToken)
    {
        var lease = await db.LeaseContracts
            .AsNoTracking()
            .Where(item => item.Id == leaseId && item.OrgId == scope.OrgId)
            .Select(item => new LeaseRow(item.Id, item.OrgId, item.PropertyId, item.StartDate, item.CurrentTermEndDate))
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw LeaseNotFound();
        if (!scope.IsOrgWide)
        {
            var visible = await db.Properties
                .Where(property => property.Id == lease.PropertyId && property.OrgId == scope.OrgId)
                .InScope(scope)
                .AnyAsync(cancellationToken);
            if (!visible)
                throw LeaseNotFound();
        }

        return lease;
    }

    private async Task<TenantCondominiumSettlement> RequireSettlementAsync(
        HostScope scope, Guid leaseId, Guid settlementId, CancellationToken cancellationToken) =>
        await db.TenantCondominiumSettlements
            .Include(item => item.Allocations)
            .FirstOrDefaultAsync(
                item => item.Id == settlementId && item.LeaseContractId == leaseId && item.OrgId == scope.OrgId,
                cancellationToken)
        ?? throw new NotFoundException("Settlement not found")
        {
            Code = TenantSettlementErrorCodes.NotFound,
            MessageKey = "TenantSettlementNotFound",
        };

    private async Task<TenantSettlementView> ToViewAsync(TenantCondominiumSettlement settlement, CancellationToken cancellationToken)
    {
        var entryId = await db.RentLedgerComponents
            .AsNoTracking()
            .Where(component => component.TenantSettlementId == settlement.Id)
            .Select(component => (Guid?)component.RentLedgerEntryId)
            .FirstOrDefaultAsync(cancellationToken);
        return new TenantSettlementView(
            settlement.Id,
            settlement.LeaseContractId,
            settlement.CondominiumPeriodId,
            settlement.StatementId,
            settlement.StatementRevision,
            settlement.RecoverableFinalAmount,
            settlement.AdvancesAccountedAmount,
            settlement.PriorTenantBalanceAmount,
            settlement.SettlementAmount,
            settlement.Direction,
            settlement.Disposition,
            settlement.Status,
            entryId,
            settlement.SentToTenantAt);
    }

    private Task<Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction?> LockLeaseAsync(
        Guid leaseId, CancellationToken cancellationToken) =>
        PostgresAdvisoryLocks.BeginLockedTransactionAsync(
            db, cancellationToken, (PostgresAdvisoryLocks.Scope.RentLease, leaseId.ToString("N")));

    private static TenantSettlementMath.Occupancy OccupancyOf(LeaseRow lease, LeaseCondominiumTerms? terms)
    {
        var start = RomeCalendar.DateInRome(lease.StartDate);
        var end = RomeCalendar.DateInRome(lease.EndDate);
        if (terms?.EffectiveFrom is { } from && from > start)
            start = from;
        if (terms?.EffectiveTo is { } to && to < end)
            end = to;
        if (end < start)
            throw Invalid();
        return new TenantSettlementMath.Occupancy(start, end);
    }

    /// <summary>
    /// Consumption and explicit amounts are matched onto the new consuntivo by description, so a replacement
    /// does not drop them and credit every advance.
    /// </summary>
    private static IReadOnlyList<ExplicitSettlementLine>? ExplicitLinesFromSnapshot(
        string snapshotJson, IReadOnlyList<TenantFinalLineView> lines)
    {
        if (lines.Count == 0 || string.IsNullOrWhiteSpace(snapshotJson))
            return null;

        var snapshot = TenantSettlementMath.Replay(snapshotJson);
        if (snapshot.Rule is not (TenantAllocationRule.Consumption or TenantAllocationRule.ExplicitAmount))
            return null;

        var remaining = lines.ToList();
        var matched = new List<ExplicitSettlementLine>();
        foreach (var old in snapshot.Lines.Where(line =>
            line.Charged && line.Rule is TenantAllocationRule.Consumption or TenantAllocationRule.ExplicitAmount))
        {
            var index = remaining.FindIndex(line =>
                !string.IsNullOrEmpty(old.Description)
                && string.Equals(line.Description, old.Description, StringComparison.Ordinal));
            if (index < 0)
                continue;

            var line = remaining[index];
            remaining.RemoveAt(index);
            matched.Add(new ExplicitSettlementLine(line.LineId, old.AllocatedAmount));
        }

        return matched.Count == 0 ? null : matched;
    }

    private async Task<IDbContextTransaction?> BeginUnitOfWorkAsync(CancellationToken cancellationToken)
    {
        if (!db.Database.IsRelational() || db.Database.CurrentTransaction is not null)
            return null;

        return await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
    }

    private static Dictionary<Guid, decimal>? ExplicitAmounts(
        IReadOnlyList<ExplicitSettlementLine>? lines, TenantFinalStatementView final)
    {
        if (lines is not { Count: > 0 })
            return null;

        var known = final.Lines.Select(line => line.LineId).ToHashSet();
        var amounts = new Dictionary<Guid, decimal>(lines.Count);
        foreach (var line in lines)
        {
            if (!known.Contains(line.LineId) || !CondominiumMoney.InRange(line.Amount) || line.Amount < 0m)
                throw Invalid();
            amounts[line.LineId] = line.Amount;
        }

        return amounts;
    }

    private static bool OpenForSettlement(RentLedgerEntry entry) =>
        entry.Status == RentLedgerStatus.Scheduled
        && entry.StripePaymentIntentId is null
        && entry.PaymentRequestedAt is null
        && !IsSettlementOnly(entry);

    private static bool Frozen(RentLedgerEntry entry) =>
        entry.Status is RentLedgerStatus.Paid or RentLedgerStatus.Processing or RentLedgerStatus.Failed
        || entry.StripePaymentIntentId is not null
        || entry.PaymentRequestedAt is not null;

    private static bool IsSettlementOnly(RentLedgerEntry entry) =>
        entry.Components.Count > 0
        && entry.Components.All(component => component.Kind == RentLedgerComponentKind.CondominiumSettlement);

    private static HashSet<DateOnly> UsedDays(IEnumerable<RentLedgerEntry> entries) =>
        entries.Select(entry => entry.PeriodStart).ToHashSet();

    private static List<decimal> Amounts(RentLedgerEntry entry, RentLedgerComponentKind kind) =>
        entry.Components.Where(component => component.Kind == kind).Select(component => component.Amount).ToList();

    private static void RequireUser(string userId)
    {
        if (string.IsNullOrWhiteSpace(userId) || userId.Length > TenantCondominiumSettlement.UserIdMaxLength)
            throw Invalid();
    }

    private static DomainRuleException Invalid() =>
        new(TenantSettlementErrorCodes.Invalid, "TenantSettlementInvalid");

    private static NotFoundException LeaseNotFound() =>
        new("Lease not found")
        {
            Code = TenantSettlementErrorCodes.LeaseNotFound,
            MessageKey = "TenantSettlementLeaseNotFound",
        };

    private sealed record LeaseRow(Guid Id, Guid OrgId, Guid PropertyId, DateTime StartDate, DateTime EndDate);

    private sealed record AdvanceRow(Guid ComponentId, Guid EntryId, decimal Amount);
}
