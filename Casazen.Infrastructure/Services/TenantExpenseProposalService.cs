using Casazen.Core.Authorization;
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
            result.SettledAtConsuntivo,
            result.PeriodId,
            result.PeriodLabel);
    }
}
