using Casazen.Core.Condominium;
using Casazen.Core.Entities.Enums;

namespace Casazen.Core.Services;

/// <summary>
/// The condominium expense the contract wizard proposes. It does not start a payment: the tenant pays on the rent
/// APIs, and the landlord pays the condominium from the bank-link payments API.
/// </summary>
public interface ITenantExpenseProposalService
{
    Task<TenantExpenseProposalView> ProposeAsync(
        Guid leaseId, ProposeTenantExpenseCommand command, CancellationToken cancellationToken = default);
}

public sealed record ProposeTenantExpenseCommand(
    CondominiumAdvanceMode? AdvanceMode,
    decimal? ContractualAdvance,
    RentCadence? Cadence,
    decimal? PreviousConsuntivo,
    decimal? Preventivo,
    decimal? ManualAmount);

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
