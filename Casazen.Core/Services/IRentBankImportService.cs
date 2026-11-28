using Casazen.Core.Authorization;
using Casazen.Core.BankStatements;

namespace Casazen.Core.Services;

public sealed record RentBankInstallment(
    Guid Id,
    Guid LeaseId,
    Guid PropertyId,
    string PropertyName,
    string? TenantName,
    DateOnly PeriodStart,
    DateOnly PeriodEnd,
    DateOnly DueDate,
    decimal Amount,
    string Currency);

/// <summary>
/// A credit of the statement that has at least one installment of its amount to settle, with the installment proposed and the other ones
/// that could be it (best first). <c>Description</c> and <c>Payer</c> are what the file says, clipped: they go back to the person who
/// uploaded the file, are never stored and never logged.
/// </summary>
public sealed record RentBankSuggestionView(
    string Fingerprint,
    DateOnly Date,
    decimal Amount,
    string Currency,
    string Description,
    string? Payer,
    RentBankConfidence Confidence,
    IReadOnlyList<RentBankReason> Reasons,
    RentBankInstallment Installment,
    IReadOnlyList<RentBankInstallment> Alternatives);

public sealed record RentBankCreditView(string Fingerprint, DateOnly Date, decimal Amount, string Currency, string Description, string? Payer);

public sealed record RentBankImportSummary(int Movements, int Credits, int AlreadyImported, int Suggested, int Confident, int Unmatched);

public sealed record RentBankImportPreview(
    BankStatementFormat Format,
    RentBankImportSummary Summary,
    IReadOnlyList<RentBankSuggestionView> Suggestions,
    IReadOnlyList<RentBankCreditView> Unmatched);

public sealed record RentBankSelection(string Fingerprint, Guid InstallmentId);

public sealed record RentBankConfirmed(string Fingerprint, Guid InstallmentId);

public sealed record RentBankSkipped(string Fingerprint, Guid InstallmentId, string Code);

public sealed record RentBankConfirmation(int Requested, IReadOnlyList<RentBankConfirmed> Confirmed, IReadOnlyList<RentBankSkipped> Skipped);

public static class RentBankImportLimits
{
    public const int MaxUnmatchedShown = 200;

    public const string OfflineNote = "Da estratto conto";
}

/// <summary>
/// Import of a bank statement on the rents of the long-term area. <see cref="PreviewAsync"/> reads the file and proposes, for each credit,
/// the installment it pays; nothing is written and the file is not kept. <see cref="ConfirmAsync"/> takes the file again with the choices of
/// the landlord and declares each installment paid through <see cref="IRentBillingService.MarkPaidOfflineAsync"/> (the same path as a
/// payment declared by hand, with the note <see cref="RentBankImportLimits.OfflineNote"/> and the value date of the credit), remembering the
/// fingerprint of the movement so that it is never imported twice. Everything is read inside the caller's scope (org and properties): the
/// installments of a property the caller does not reach are neither proposed nor changed.
/// </summary>
public interface IRentBankImportService
{
    Task<RentBankImportPreview> PreviewAsync(
        HostScope scope, byte[] file, BankColumnMapping? mapping, CancellationToken cancellationToken = default);

    Task<RentBankConfirmation> ConfirmAsync(
        HostScope scope,
        byte[] file,
        BankColumnMapping? mapping,
        IReadOnlyList<RentBankSelection> selections,
        string userId,
        Func<Guid, CancellationToken, Task<bool>> mayChange,
        CancellationToken cancellationToken = default);
}
