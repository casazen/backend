using Casazen.Core.Authorization;
using Casazen.Core.Entities.Enums;

namespace Casazen.Core.Services;

/// <summary>
/// The tenant's consuntivo settlement. It asks the condominium book for the confirmed final and posts a
/// <c>CondominiumSettlement</c> component on the rent ledger. It does not read the condominium tables and it does not
/// email the tenant: sending stays a manual step.
/// </summary>
public interface ITenantCondominiumSettlementService
{
    Task<TenantSettlementView> CalculateAsync(
        HostScope scope, Guid leaseId, CalculateTenantSettlementCommand command, string userId, CancellationToken cancellationToken = default);

    Task<TenantSettlementView> ConfirmAsync(
        HostScope scope, Guid leaseId, Guid settlementId, string userId, CancellationToken cancellationToken = default);

    Task<TenantSettlementView> PostAsync(
        HostScope scope, Guid leaseId, Guid settlementId, string userId, CancellationToken cancellationToken = default);

    /// <summary>
    /// A new confirmed consuntivo reverses a settlement already posted for that year and posts the replacement.
    /// Calling it again for the same consuntivo does not post a second one. A year with no posted settlement is left alone.
    /// </summary>
    Task<IReadOnlyList<TenantSettlementView>> ReplacePostedForPeriodAsync(
        HostScope scope, Guid periodId, string userId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Confirms the consuntivo and replaces every posted tenant settlement of that year in one transaction.
    /// A failure rolls the new revision back with the settlements, so a retry does not open another revision.
    /// </summary>
    Task<FinalConfirmation> ConfirmFinalAndReplaceAsync(
        HostScope scope,
        Guid condominiumId,
        Guid periodId,
        ConfirmFinalCommand command,
        string userId,
        CancellationToken cancellationToken = default);
}

public sealed record ExplicitSettlementLine(Guid LineId, decimal Amount);

public sealed record CalculateTenantSettlementCommand(
    Guid PeriodId,
    TenantSettlementDisposition? Disposition,
    TenantAllocationRule? Rule,
    IReadOnlyList<ExplicitSettlementLine>? ExplicitLines);

public sealed record TenantSettlementView(
    Guid Id,
    Guid LeaseContractId,
    Guid PeriodId,
    Guid StatementId,
    int StatementRevision,
    decimal RecoverableFinalAmount,
    decimal AdvancesAccountedAmount,
    decimal PriorTenantBalanceAmount,
    decimal SettlementAmount,
    TenantSettlementDirection Direction,
    TenantSettlementDisposition Disposition,
    TenantSettlementStatus Status,
    Guid? RentLedgerEntryId,
    DateTime? SentToTenantAt);

public static class TenantSettlementErrorCodes
{
    public const string NotFound = "tenant_settlement_not_found";
    public const string LeaseNotFound = "tenant_settlement_lease_not_found";
    public const string Invalid = "tenant_settlement_invalid";
    public const string NotConfirmed = "tenant_settlement_not_confirmed";
    public const string NoOpenInstallment = "tenant_settlement_no_open_installment";
    public const string NoSchedule = "tenant_settlement_no_schedule";
    public const string RefundNotCredit = "tenant_settlement_refund_not_credit";
    public const string CreditExceedsInstallment = "tenant_settlement_credit_exceeds_installment";
}
