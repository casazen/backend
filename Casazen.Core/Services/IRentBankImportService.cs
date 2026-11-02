using Casazen.Core.Authorization;
using Casazen.Core.BankStatements;

namespace Casazen.Core.Services;

/// <summary>
/// A rent installment as the landlord reads it on the screen of the import: where it is, who owes it, which period it pays and for how much.
/// The tenant is the first tenant of the lease, by name only (D29), and is null once that tenant was anonymized.
/// </summary>
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

/// <summary>A credit with no installment to settle: shown so the landlord can see why a payment was not matched.</summary>
public sealed record RentBankCreditView(string Fingerprint, DateOnly Date, decimal Amount, string Currency, string Description, string? Payer);

/// <summary>
/// The numbers of the file: movements in all, the credits among them, the ones imported before, the ones with a suggestion (<c>Confident</c>
/// of them sure enough for the one-click confirmation) and the ones left without.
/// </summary>
public sealed record RentBankImportSummary(int Movements, int Credits, int AlreadyImported, int Suggested, int Confident, int Unmatched);

/// <summary>
/// What a statement says about the rents of the landlord's org: the credits already imported are left out, the others are matched to the
/// installments still to collect. <c>Unmatched</c> lists at most <see cref="RentBankImportLimits.MaxUnmatchedShown"/> of them, the latest first.
/// Nothing is written.
/// </summary>
public sealed record RentBankImportPreview(
    BankStatementFormat Format,
    RentBankImportSummary Summary,
    IReadOnlyList<RentBankSuggestionView> Suggestions,
    IReadOnlyList<RentBankCreditView> Unmatched);

/// <summary>The landlord's choice: this credit (by fingerprint) settles this installment.</summary>
public sealed record RentBankSelection(string Fingerprint, Guid InstallmentId);

/// <summary>A choice that was applied: the installment is paid, and the credit will not be offered again.</summary>
public sealed record RentBankConfirmed(string Fingerprint, Guid InstallmentId);

/// <summary>A choice that was not applied, and why (the stable code of the refusal): it never stops the others.</summary>
public sealed record RentBankSkipped(string Fingerprint, Guid InstallmentId, string Code);

/// <summary>The outcome of a confirmation: how many choices were sent, the ones applied and the ones skipped with their code.</summary>
public sealed record RentBankConfirmation(int Requested, IReadOnlyList<RentBankConfirmed> Confirmed, IReadOnlyList<RentBankSkipped> Skipped);

/// <summary>Sizes of the answers of the import.</summary>
public static class RentBankImportLimits
{
    /// <summary>Most credits without an installment that the preview lists (the summary counts them all).</summary>
    public const int MaxUnmatchedShown = 200;

    /// <summary>The note written on a payment confirmed from a statement: where it came from, as the landlord would write it.</summary>
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
    /// <summary>
    /// Reads <paramref name="file"/> and matches its credits to the installments still to collect that <paramref name="scope"/> reaches.
    /// Refusals are the ones of <see cref="BankStatementErrorCodes"/> (413, 415, 422).
    /// </summary>
    Task<RentBankImportPreview> PreviewAsync(
        HostScope scope, byte[] file, BankColumnMapping? mapping, CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads <paramref name="file"/> again and declares paid the installments of <paramref name="selections"/>, each with the credit
    /// whose fingerprint is named. A selection that cannot be applied (the credit is not in the file, was imported before, has another
    /// amount, the installment is not the caller's or cannot be paid any more) is skipped with its code. <paramref name="mayChange"/> says
    /// whether the caller may change the installments of a property (it is asked once per property).
    /// </summary>
    Task<RentBankConfirmation> ConfirmAsync(
        HostScope scope,
        byte[] file,
        BankColumnMapping? mapping,
        IReadOnlyList<RentBankSelection> selections,
        string userId,
        Func<Guid, CancellationToken, Task<bool>> mayChange,
        CancellationToken cancellationToken = default);
}
