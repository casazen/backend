using Casazen.Core.Authorization;
using Casazen.Core.Condominium;
using Casazen.Core.Entities.Enums;

namespace Casazen.Core.Services;

public interface IAdministratorStatementReaderService
{
    Task<StatementProposalView> ProposeAsync(
        HostScope scope, Guid condominiumId, StatementFileUpload file, string userId, CancellationToken cancellationToken = default);

    Task<AdministratorInstallmentReader.Reading> ReadInstallmentsAsync(
        HostScope scope, Guid condominiumId, StatementFileUpload file, string userId, CancellationToken cancellationToken = default);

    Task<StatementProposalView> ConfirmAsync(
        HostScope scope, Guid condominiumId, Guid proposalId, string userId, CancellationToken cancellationToken = default);
}

public sealed record StatementProposalView(
    Guid Id,
    Guid? PeriodId,
    string Label,
    CondominiumStatementKind Kind,
    decimal? Ordinary,
    decimal? Extraordinary,
    string? ExtraordinaryDescription,
    decimal? FinalAmount,
    PriorYearBalanceDisposition? Settlement,
    decimal Confidence,
    bool ChargesTenant,
    string Excerpt,
    IReadOnlyList<string> NotExtracted,
    bool Posted,
    Guid? StatementId);
