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
    Task<IReadOnlyList<PropertyCondominiumView>> ListPropertiesAsync(HostScope scope, CancellationToken cancellationToken = default);

    Task<CondominiumView> SetupPropertyAsync(HostScope scope, Guid propertyId, CreateCondominiumCommand command, string userId, CancellationToken cancellationToken = default);

    Task<CondominiumView> CreateCondominiumAsync(HostScope scope, CreateCondominiumCommand command, string userId, CancellationToken cancellationToken = default);

    Task<CondominiumUnitView> LinkPropertyAsync(HostScope scope, Guid condominiumId, LinkPropertyCommand command, string userId, CancellationToken cancellationToken = default);

    Task<CondominiumPeriodView> CreatePeriodAsync(HostScope scope, Guid condominiumId, CreateCondominiumPeriodCommand command, string userId, CancellationToken cancellationToken = default);

    Task<BudgetConfirmation> ConfirmBudgetAsync(HostScope scope, Guid condominiumId, Guid periodId, ConfirmBudgetCommand command, string userId, CancellationToken cancellationToken = default);

    Task<FinalConfirmation> ConfirmFinalAsync(HostScope scope, Guid condominiumId, Guid periodId, ConfirmFinalCommand command, string userId, CancellationToken cancellationToken = default);

    /// <summary>
    /// The confirmed consuntivo of <paramref name="periodId"/> for one property. The rent ledger calls this instead of
    /// reading the condominium tables. Lines of another unit are left out. A building-wide line is included only when
    /// the condominium has this single unit, because nothing is split by thousandths.
    /// </summary>
    Task<TenantFinalStatementView> GetConfirmedFinalForPropertyAsync(
        HostScope scope, Guid propertyId, Guid periodId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Whether <paramref name="propertyId"/> already has a condominium year, and the tenant-chargeable total of its
    /// confirmed budget. Extraordinary lines still in review are not included. Null budget means the year exists but
    /// nothing chargeable is confirmed.
    /// </summary>
    Task<TenantBudgetBasis> GetTenantBudgetBasisAsync(HostScope scope, Guid propertyId, CancellationToken cancellationToken = default);

    /// <summary>The amount still due on one owner installment. The bank-link payments API debits this, not the contract.</summary>
    Task<CondominiumInstallmentDue> GetInstallmentDueAsync(
        HostScope scope, Guid condominiumId, Guid installmentId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Records a SEPA debit that Stripe has confirmed, once. The same <paramref name="externalSourceHash"/> does not
    /// post a second payment.
    /// </summary>
    Task<CondominiumPaymentView> RecordBankLinkInstallmentPaymentAsync(
        HostScope scope,
        Guid installmentId,
        decimal amount,
        DateOnly paidOn,
        string externalSourceHash,
        string userId,
        CancellationToken cancellationToken = default);

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
    CondominiumInstallmentCadence Cadence,
    bool OrdinaryNeedsReview = false,
    IReadOnlyList<DateOnly>? CustomDueDates = null);

public sealed record FinalExpenseLineCommand(
    string Description,
    decimal Amount,
    CondominiumExpenseNature Nature,
    TenantRecoverability? Recoverability,
    decimal? RecoverablePercentage,
    decimal? Confidence,
    ExpenseClassificationSource Source,
    DateOnly? CompetenceStart,
    DateOnly? CompetenceEnd,
    ExpenseReviewStatus? ReviewStatus);

public sealed record ConfirmFinalCommand(
    decimal Amount,
    PriorYearBalanceDisposition Settlement,
    IReadOnlyList<FinalExpenseLineCommand>? Lines = null);

public sealed record TenantFinalLineView(
    Guid LineId,
    string Description,
    CondominiumExpenseNature Nature,
    TenantRecoverability Recoverability,
    ExpenseReviewStatus ReviewStatus,
    ExpenseClassificationSource Source,
    decimal? Confidence,
    decimal Amount,
    decimal RecoverableAmount,
    DateOnly CompetenceStart,
    DateOnly CompetenceEnd,
    bool ChargesTenant);

public sealed record TenantBudgetBasis(bool HasFiscalYear, Guid? PeriodId, string? Label, decimal? RecoverableAnnual);

public sealed record CondominiumInstallmentDue(Guid CondominiumId, Guid PeriodId, Guid InstallmentId, decimal AmountDue);

public sealed record TenantFinalStatementView(
    Guid CondominiumId,
    Guid PeriodId,
    string Label,
    DateOnly PeriodStart,
    DateOnly PeriodEnd,
    Guid StatementId,
    int RevisionNumber,
    IReadOnlyList<TenantFinalLineView> Lines);

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
    public const string FinalNotConfirmed = "condominium_final_not_confirmed";
    public const string NeedsWholeOrg = "condominium_needs_whole_org";
}

public sealed record PropertyCondominiumView(Guid PropertyId, string PropertyName, IReadOnlyList<PropertyCondominiumBook> Books);
public sealed record PropertyCondominiumBook(Guid Id, string Name, int FiscalStartMonth, IReadOnlyList<CondominiumPeriodView> Periods);
