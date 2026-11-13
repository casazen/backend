using Casazen.Core.Condominium;
using Casazen.Core.Entities.Enums;

namespace Casazen.Core.Services;

/// <summary>
/// The condominium expense the contract wizard proposes. It does not start a payment: the tenant pays on the rent
/// APIs, and the landlord records external payments in the condominium book.
/// </summary>
public interface ITenantExpenseProposalService
{
    Task<TenantExpenseProposalView?> GetSavedAsync(Guid leaseId, CancellationToken cancellationToken = default);
    Task SaveAsync(Guid leaseId, TenantExpenseProposalView proposal, ProposeTenantExpenseCommand command, string userId, CancellationToken cancellationToken = default);

    Task<TenantExpenseProposalView> ProposeAsync(
        Guid leaseId, ProposeTenantExpenseCommand command, CancellationToken cancellationToken = default);
}

public sealed record ProposeTenantExpenseCommand(
    CondominiumAdvanceMode? AdvanceMode,
    decimal? ContractualAdvance,
    RentCadence? Cadence,
    decimal? PreviousConsuntivo,
    decimal? Preventivo,
    decimal? ManualAmount,
    bool? SettlementRequired = null);

public sealed record TenantExpenseProposalView(
    TenantExpenseProposal.Source Source,
    decimal InstallmentAmount,
    decimal LastInstallmentAmount,
    int Installments,
    decimal AnnualAmount,
    decimal? PreviousConsuntivo,
    decimal? Preventivo,
    bool SettledAtConsuntivo,
    Guid? PeriodId,
    string? PeriodLabel);
