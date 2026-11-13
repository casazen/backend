using Casazen.Core.Authorization;
using System.Text.Json;
using Casazen.Infrastructure.Services.LeaseDrafts;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Condominium;
using Casazen.Core.Exceptions;
using Casazen.Core.Services;
using Microsoft.EntityFrameworkCore;
using Casazen.Infrastructure.Data;

namespace Casazen.Infrastructure.Services;

/// <summary>
/// Proposes the condominium expense of a lease from the condominium book, or from the two amounts the wizard types
/// when the book is empty. No payment is started here.
/// </summary>
public sealed class TenantExpenseProposalService(AppDbContext db, ICondominiumAccountingService condominiums)
    : ITenantExpenseProposalService
{
    public async Task<TenantExpenseProposalView> ProposeAsync(
        Guid leaseId, ProposeTenantExpenseCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        var lease = await db.LeaseContracts
            .AsNoTracking()
            .Where(item => item.Id == leaseId)
            .Select(item => new { item.OrgId, item.PropertyId })
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw new NotFoundException("Lease not found")
            {
                Code = "lease_not_found",
                MessageKey = "LeaseNotFound",
            };

        var basis = await condominiums.GetTenantBudgetBasisAsync(new HostScope(lease.OrgId), lease.PropertyId, cancellationToken);
        TenantExpenseProposal.Result result;
        try
        {
            result = TenantExpenseProposal.Resolve(
                basis.HasFiscalYear,
                basis.PeriodId,
                basis.Label,
                basis.RecoverableAnnual,
                new TenantExpenseProposal.Request(
                    command.AdvanceMode,
                    command.ContractualAdvance,
                    command.Cadence,
                    command.PreviousConsuntivo,
                    command.Preventivo,
                    command.ManualAmount));
        }
        catch (ArgumentException)
        {
            throw new DomainRuleException("tenant_expense_proposal_invalid", "TenantExpenseProposalInvalid");
        }

        return new TenantExpenseProposalView(
            result.Source,
            result.InstallmentAmount,
            result.LastInstallmentAmount,
            result.Installments,
            result.AnnualAmount,
            result.PreviousConsuntivo,
            result.Preventivo,
            command.SettlementRequired ?? result.SettledAtConsuntivo,
            result.PeriodId,
            result.PeriodLabel);
    }

    public async Task<TenantExpenseProposalView?> GetSavedAsync(Guid leaseId, CancellationToken cancellationToken = default)
    {
        var terms = await db.LeaseCondominiumTerms.AsNoTracking().SingleOrDefaultAsync(t => t.LeaseContractId == leaseId, cancellationToken);
        if (terms?.AdvanceAmount is not > 0m || terms.Cadence is null || terms.ConfirmedAt is null) return null;
        var saved = await ReadChoiceAsync(db, leaseId, cancellationToken);
        if (saved is not null && saved.InstallmentAmount == terms.AdvanceAmount && saved.SettledAtConsuntivo == terms.SettlementRequired
            && saved.Installments == TenantExpenseProposal.InstallmentCount(terms.Cadence.Value)) return saved;
        var count = TenantExpenseProposal.InstallmentCount(terms.Cadence.Value);
        return new(terms.AdvanceMode == CondominiumAdvanceMode.BudgetAllocation ? TenantExpenseProposal.Source.ConfirmedBudget : TenantExpenseProposal.Source.Manual,
            terms.AdvanceAmount.Value, terms.AdvanceAmount.Value, count, terms.AdvanceAmount.Value * count,
            null, null, terms.SettlementRequired, null, null);
    }

    public async Task SaveAsync(Guid leaseId, TenantExpenseProposalView proposal, ProposeTenantExpenseCommand command,
        string userId, CancellationToken cancellationToken = default)
    {
        await using var transaction = await PostgresAdvisoryLocks.BeginLockedTransactionAsync(db, cancellationToken,
            (PostgresAdvisoryLocks.Scope.RentLease, leaseId.ToString("N")));
        await LeaseDraftLocking.LockRowAsync(db, leaseId, cancellationToken);
        var lease = await db.LeaseContracts.SingleAsync(l => l.Id == leaseId, cancellationToken);
        await db.Entry(lease).ReloadAsync(cancellationToken);
        if (lease.Status != LeaseStatus.Draft)
            throw new DomainRuleException("lease_not_draft", "LeaseNotDraft");
        var terms = await db.LeaseCondominiumTerms.SingleOrDefaultAsync(t => t.LeaseContractId == leaseId, cancellationToken);
        if (terms is null)
        {
            terms = new LeaseCondominiumTerms { OrgId = lease.OrgId, LeaseContractId = leaseId };
            db.LeaseCondominiumTerms.Add(terms);
        }
        terms.AdvanceMode = proposal.Source is TenantExpenseProposal.Source.ConfirmedBudget or TenantExpenseProposal.Source.Statements
            ? CondominiumAdvanceMode.BudgetAllocation : CondominiumAdvanceMode.FixedContractAmount;
        terms.AdvanceAmount = proposal.InstallmentAmount;
        terms.Cadence = command.Cadence ?? RentCadence.Monthly;
        terms.SettlementRequired = proposal.SettledAtConsuntivo;
        terms.ConfirmedAt = DateTime.UtcNow;
        terms.ConfirmedByUserId = userId;
        terms.UpdatedAt = DateTime.UtcNow;
        db.LeaseEvents.Add(new LeaseEvent
        {
            LeaseContractId = leaseId,
            EventType = LeaseEventType.CondominiumExpenseChosen,
            OccurredAt = terms.UpdatedAt,
            Payload = JsonSerializer.Serialize(proposal)
        });
        if (lease.ReviewConfirmedAt is not null)
        {
            lease.ReviewConfirmedAt = null;
            lease.ReviewFingerprint = null;
            db.LeaseEvents.Add(new LeaseEvent
            {
                LeaseContractId = leaseId,
                EventType = LeaseEventType.ReviewReopened,
                Payload = "condominium_expense_changed"
            });
            if (lease.SentForSignatureAt is not null)
            {
                lease.SentForSignatureAt = null;
                db.LeaseEvents.Add(new LeaseEvent
                {
                    LeaseContractId = leaseId,
                    EventType = LeaseEventType.SentRevoked,
                    Payload = "review_reopened"
                });
            }
        }
        await db.SaveChangesAsync(cancellationToken);
        if (transaction is not null) await transaction.CommitAsync(cancellationToken);
    }

    internal static async Task<TenantExpenseProposalView?> ReadChoiceAsync(AppDbContext db, Guid leaseId, CancellationToken ct)
    {
        var payload = await db.LeaseEvents.AsNoTracking()
            .Where(e => e.LeaseContractId == leaseId && e.EventType == LeaseEventType.CondominiumExpenseChosen)
            .OrderByDescending(e => e.OccurredAt).ThenByDescending(e => e.Id).Select(e => e.Payload).FirstOrDefaultAsync(ct);
        return payload is null ? null : JsonSerializer.Deserialize<TenantExpenseProposalView>(payload);
    }
}
