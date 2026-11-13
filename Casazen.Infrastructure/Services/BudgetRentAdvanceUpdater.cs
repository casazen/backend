using Casazen.Core.Condominium;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Leases;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Casazen.Infrastructure.Services;

/// <summary>Confirmed tenant-recoverable budgets update only unquoted, unpaid installments.
/// A fixed contractual advance without settlement always wins. A settlement clause permits
/// recalculation in the year after a posted final. Extraordinary or uncertain lines never become automatic rent charges.</summary>
internal static class BudgetRentAdvanceUpdater
{
    public static async Task RefreshAsync(AppDbContext db, Guid leaseId, CancellationToken ct)
    {
        await using var tx = await PostgresAdvisoryLocks.BeginLockedTransactionAsync(db, ct,
            (PostgresAdvisoryLocks.Scope.RentLease, leaseId.ToString("N")));
        var lease = await db.LeaseContracts.Include(l => l.RentSchedule).SingleAsync(l => l.Id == leaseId, ct);
        var terms = await db.LeaseCondominiumTerms.SingleOrDefaultAsync(t => t.LeaseContractId == leaseId, ct);
        if (terms?.ConfirmedAt is null
            || (terms.AdvanceMode != CondominiumAdvanceMode.BudgetAllocation && !(terms.AdvanceMode == CondominiumAdvanceMode.FixedContractAmount && terms.SettlementRequired))
            || lease.RentSchedule is not { IsActive: true } schedule) return;
        var units = await db.CondominiumUnits.Where(u => u.OrgId == lease.OrgId && u.PropertyId == lease.PropertyId).ToListAsync(ct);

        var condoIds = units.Select(u => u.CondominiumId).Distinct().ToArray();
        var budgets = await db.CondominiumStatements.Include(s => s.Period).Include(s => s.Lines)
            .Where(s => s.OrgId == lease.OrgId && s.Kind == CondominiumStatementKind.Budget
                && s.Status == CondominiumStatementStatus.Confirmed && condoIds.Contains(s.Period.CondominiumId))
            .ToListAsync(ct);
        var entries = await db.RentLedgerEntries.Include(e => e.Components).Where(e => e.LeaseContractId == leaseId
            && e.OrgId == lease.OrgId && e.StripePaymentIntentId == null && e.PaymentRequestedAt == null
            && (e.Status == RentLedgerStatus.Scheduled || e.Status == RentLedgerStatus.Failed)).ToListAsync(ct);
        var carry = await db.TenantCondominiumSettlements.AsNoTracking()
            .Where(s => s.OrgId == lease.OrgId && s.LeaseContractId == leaseId
                && s.Disposition == TenantSettlementDisposition.CarryForward
                && (s.Status == TenantSettlementStatus.Posted || s.Status == TenantSettlementStatus.Settled))
            .ToListAsync(ct);
        var choice = terms.AdvanceMode == CondominiumAdvanceMode.BudgetAllocation
            ? await TenantExpenseProposalService.ReadChoiceAsync(db, leaseId, ct) : null;
        if (choice is not null && (terms.Cadence is null || choice.InstallmentAmount != terms.AdvanceAmount
            || choice.SettledAtConsuntivo != terms.SettlementRequired
            || choice.Installments != TenantExpenseProposal.InstallmentCount(terms.Cadence.Value))) choice = null;
        if (choice is not null)
        {
            var months = RentInstallmentPlan.MonthsPer(schedule.Cadence);
            var shares = RentLedgerMoney.SplitRemainderOnLast(choice.AnnualAmount, 12 / months);
            var start = DateOnly.FromDateTime(lease.StartDate);
            foreach (var entry in entries.Where(e => !budgets.Any(b => e.PeriodStart >= b.Period.StartDate && e.PeriodStart <= b.Period.EndDate)))
            {
                if (entry.Components.Count > 0 && entry.Components.All(c => c.Kind == RentLedgerComponentKind.CondominiumSettlement)) continue;
                var elapsed = (entry.PeriodStart.Year - start.Year) * 12 + entry.PeriodStart.Month - start.Month;
                if (elapsed < 0 || elapsed % months != 0) continue;
                ApplyAdvance(db, lease.OrgId, entry, shares[(elapsed % 12) / months], null, "Acconto condominiale concordato nel contratto");
            }
        }
        foreach (var budget in budgets.OrderBy(b => b.Period.StartDate))
        {
            var previous = carry.Where(s => s.PeriodEnd.AddDays(1) == budget.Period.StartDate).ToList();
            // A manually agreed advance continues throughout its first year. Only a settled final
            // statement permits changing it for the following year.
            if (terms.AdvanceMode == CondominiumAdvanceMode.FixedContractAmount && previous.Count == 0) continue;
            var unitIds = units.Where(u => u.CondominiumId == budget.Period.CondominiumId).Select(u => u.Id).ToHashSet();
            var count = await db.CondominiumUnits.CountAsync(u => u.CondominiumId == budget.Period.CondominiumId, ct);
            var annual = budget.Lines.Where(l => (l.CondominiumUnitId is { } id && unitIds.Contains(id) || l.CondominiumUnitId is null && count == 1)
                && ExpenseClassification.ChargesStored(l.ExpenseNature, l.TenantRecoverability, l.ReviewStatus, l.RecoverableAmount, l.Confidence))
                .Sum(l => l.RecoverableAmount);
            annual += previous.Sum(s => s.SettlementAmount);
            // A credit can cover the full advance. Its unused remainder is carried by the next settlement.
            annual = Math.Max(0m, annual);
            var monthly = RentLedgerMoney.SplitRemainderOnLast(annual, 12);
            foreach (var entry in entries.Where(e => e.PeriodStart >= budget.Period.StartDate && e.PeriodStart <= budget.Period.EndDate))
            {
                if (entry.Components.Count > 0 && entry.Components.All(c => c.Kind == RentLedgerComponentKind.CondominiumSettlement)) continue;
                var offset = (entry.PeriodStart.Year - budget.Period.StartDate.Year) * 12 + entry.PeriodStart.Month - budget.Period.StartDate.Month;
                var months = RentInstallmentPlan.MonthsPer(schedule.Cadence);
                if (offset < 0 || offset + months > 12) continue;
                var advance = monthly.Skip(offset).Take(months).Sum();
                ApplyAdvance(db, lease.OrgId, entry, advance, budget.Id,
                    $"Preventivo {budget.Period.Label}" + (previous.Count > 0 ? " · conguaglio esercizio precedente ripartito" : ""));
            }
        }
        await db.SaveChangesAsync(ct);
        if (tx is not null) await tx.CommitAsync(ct);
    }

    private static void ApplyAdvance(AppDbContext db, Guid orgId, RentLedgerEntry entry, decimal advance, Guid? sourceId, string description)
    {
        var existing = entry.Components.Where(c => c.Kind == RentLedgerComponentKind.CondominiumAdvance).ToList();
        if (entry.Components.Count == 0)
        {
            db.RentLedgerComponents.Add(new RentLedgerComponent
            {
                Id = Guid.Empty,
                OrgId = orgId,
                RentLedgerEntry = entry,
                Kind = RentLedgerComponentKind.Rent,
                Amount = entry.AmountDue,
                CompetenceStart = entry.PeriodStart,
                CompetenceEnd = entry.PeriodEnd
            });
        }
        entry.AmountDue = entry.AmountDue - existing.Sum(c => c.Amount) + advance;
        db.RentLedgerComponents.RemoveRange(existing);
        db.RentLedgerComponents.Add(new RentLedgerComponent
        {
            Id = Guid.Empty,
            OrgId = orgId,
            RentLedgerEntry = entry,
            Kind = RentLedgerComponentKind.CondominiumAdvance,
            Amount = advance,
            SourceId = sourceId,
            CompetenceStart = entry.PeriodStart,
            CompetenceEnd = entry.PeriodEnd,
            Description = description
        });
    }
}
