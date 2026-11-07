using Casazen.Core.Authorization;
using Casazen.Core.Condominium;
using Casazen.Core.Entities.Enums;

namespace Casazen.Core.Services;

/// <summary>
/// The owner's condominium book (CondoLedger), inside CasaZen. The rent ledger does not read these tables:
/// tenant charges stay on the rent ledger until a later posting step calls this service.
/// </summary>
public interface ICondominiumAccountingService
{
    Task<CondominiumView> CreateCondominiumAsync(HostScope scope, CreateCondominiumCommand command, string userId, CancellationToken cancellationToken = default);

    Task<CondominiumUnitView> LinkPropertyAsync(HostScope scope, Guid condominiumId, LinkPropertyCommand command, string userId, CancellationToken cancellationToken = default);

    Task<CondominiumPeriodView> CreatePeriodAsync(HostScope scope, Guid condominiumId, CreateCondominiumPeriodCommand command, string userId, CancellationToken cancellationToken = default);

    Task<BudgetConfirmation> ConfirmBudgetAsync(HostScope scope, Guid condominiumId, Guid periodId, ConfirmBudgetCommand command, string userId, CancellationToken cancellationToken = default);

    Task<FinalConfirmation> ConfirmFinalAsync(HostScope scope, Guid condominiumId, Guid periodId, ConfirmFinalCommand command, string userId, CancellationToken cancellationToken = default);

    Task<PriorYearBalanceView> RecordPriorYearBalanceAsync(HostScope scope, Guid condominiumId, Guid periodId, RecordPriorYearBalanceCommand command, string userId, CancellationToken cancellationToken = default);

    Task<CondominiumPaymentView> RecordPaymentAsync(HostScope scope, Guid condominiumId, Guid periodId, RecordCondominiumPaymentCommand command, string userId, CancellationToken cancellationToken = default);

    Task<SituazioneReport.Year> GetReportAsync(HostScope scope, Guid condominiumId, Guid periodId, CancellationToken cancellationToken = default);

    Task<byte[]> RenderPaidPdfAsync(HostScope scope, Guid condominiumId, Guid periodId, CancellationToken cancellationToken = default);

    Task<byte[]> RenderUnpaidPdfAsync(HostScope scope, Guid condominiumId, Guid periodId, CancellationToken cancellationToken = default);

    Task<StatementImportResult> ImportStatementFileAsync(HostScope scope, Guid condominiumId, StatementFileUpload file, string userId, CancellationToken cancellationToken = default);
}

public sealed record CreateCondominiumCommand(
    string Name,
    string? AdministratorName,
    string? AdministratorEmail,
    string? AdministratorPhone,
    int FiscalStartMonth);

public sealed record LinkPropertyCommand(
    Guid PropertyId,
    string? UnitReference,
    decimal? Millesimi,
    DateOnly? ValidFrom,
    DateOnly? ValidTo);

public sealed record CreateCondominiumPeriodCommand(string Label);

public sealed record ExtraordinaryExpenseCommand(decimal Amount, string? Description);

public sealed record ConfirmBudgetCommand(
    decimal OrdinaryAmount,
    string? Description,
    IReadOnlyList<ExtraordinaryExpenseCommand>? Extraordinary,
    CondominiumInstallmentCadence Cadence);

public sealed record ConfirmFinalCommand(decimal Amount, PriorYearBalanceDisposition Settlement);

public sealed record RecordPriorYearBalanceCommand(
    decimal Amount,
    PriorYearBalanceDisposition Disposition,
    Guid? SourcePeriodId,
    string? Description);

public sealed record RecordCondominiumPaymentCommand(
    Guid? InstallmentId,
    bool CoversPriorBalance,
    decimal Amount,
    DateOnly PaidOn,
    string? Method,
    Guid? BankMovementId,
    string? ExternalSourceHash,
    string? Note);

public sealed record StatementFileUpload(string? FileName, byte[] Content);

public sealed record CondominiumView(Guid Id, string Name, int FiscalStartMonth);

public sealed record CondominiumUnitView(Guid Id, Guid PropertyId, string? UnitReference, decimal? Millesimi);

public sealed record CondominiumPeriodView(Guid Id, string Label, DateOnly StartDate, DateOnly EndDate, CondominiumPeriodStatus Status);

public sealed record CondominiumStatementView(Guid Id, CondominiumStatementKind Kind, int RevisionNumber, decimal TotalAmount);

public sealed record CondominiumInstallmentView(
    Guid Id,
    int SlotIndex,
    string Label,
    DateOnly DueDate,
    decimal OrdinaryAmount,
    decimal PriorSettlementAmount,
    decimal ExtraordinaryAmount,
    decimal TotalAmount);

public sealed record BudgetConfirmation(
    CondominiumStatementView Statement,
    bool InstallmentsLeftAsIssued,
    IReadOnlyList<CondominiumInstallmentView> Installments);

public sealed record FinalConfirmation(CondominiumStatementView Statement, PriorYearBalanceView? NextYearBalance);

public sealed record PriorYearBalanceView(Guid Id, Guid PeriodId, decimal Amount, PriorYearBalanceDisposition Disposition);

public sealed record CondominiumPaymentView(Guid Id, decimal Amount, DateOnly PaidOn, string? ExternalSourceHash, bool AlreadyRecorded);

public sealed record StatementImportResult(Guid PeriodId, CondominiumStatementKind Kind, Guid StatementId);

public static class CondominiumErrorCodes
{
    public const string NotFound = "condominium_not_found";
    public const string PropertyNotFound = "condominium_property_not_found";
    public const string PeriodNotFound = "condominium_period_not_found";
    public const string PeriodInvalid = "condominium_period_invalid";
    public const string PeriodOverlap = "condominium_period_overlap";
    public const string StatementInvalid = "condominium_statement_invalid";
    public const string InstallmentNotFound = "condominium_installment_not_found";
    public const string PaymentInvalid = "condominium_payment_invalid";
    public const string PaymentDuplicate = "condominium_payment_duplicate";
    public const string PriorBalanceInvalid = "condominium_prior_balance_invalid";
    public const string FileUnsupported = "condominium_statement_file_unsupported";
    public const string FileInvalid = "condominium_statement_file_invalid";
    public const string PeriodClosed = "condominium_period_closed";
    public const string NeedsWholeOrg = "condominium_needs_whole_org";
}
