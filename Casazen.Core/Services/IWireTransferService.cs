using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;

namespace Casazen.Core.Services;

/// <summary>
/// Wire transfer service (ADR-017, E16/F01/US01/T01, E16/F01/US02/T01): builds the bank file that pays the owners from their <b>confirmed payouts</b> (the net after
/// the withholding, <see cref="OwnerPayout"/>), never from a statement. CHF is the Swiss <c>pain.001.001.09.ch.03</c> file, EUR the ISO 20022 <c>pain.001.001.09</c>
/// file with the rules of the SEPA credit transfer of the EPC (service level <c>SEPA</c>, charge bearer <c>SLEV</c>, no BIC, structured addresses). One transfer for
/// each payout, one file per currency, the debtor and the debit account those of the issuer profile of the org. A payout is paid by one batch only. IBANs are
/// validated with check digits. A file that does not validate against the official schema of its format is never saved, and a file is never produced without its
/// validation (<see cref="WireTransferErrors"/>).
/// </summary>
/// <remarks>
/// Every method takes the org of the caller and filters on it explicitly: nothing relies only on the global tenant filter of the context. A batch of another org
/// does not exist for the caller (404).
/// </remarks>
public interface IWireTransferService
{
    /// <summary>
    /// Create a wire batch from the given confirmed payouts. Validates that every payout belongs to the caller's org, is confirmed (never reversed), comes from a
    /// published statement version, is in the same currency (EUR or CHF), has a net amount above zero with at most two decimals and is in no other batch; that
    /// the issuer profile of the org has its name, structured address and a valid IBAN (a Swiss or Liechtenstein one for a CHF batch, whose bank is Swiss); that
    /// every owner has a valid IBAN (never a QR-IBAN; in the area of the file: Swiss or Liechtenstein in CHF, the SEPA area in EUR) and a structured address; and
    /// that the file validates against the official schema of its format and the rules of its message: all before anything is saved (<see cref="WireTransferErrors"/>).
    /// The batch, its payouts and its file are written by one save. Returns the batch and the XML file content.
    /// </summary>
    /// <param name="orgId">The caller's org ID (from tenant context).</param>
    /// <param name="payoutIds">IDs of the confirmed payouts to pay. An empty list is never «all the payouts».</param>
    /// <param name="executionDate">The execution date asked of the bank: today (Europe/Rome) or later, at most one year ahead.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<(WireBatch Batch, string XmlContent)> CreateBatchAsync(
        Guid orgId,
        IReadOnlyList<Guid> payoutIds,
        DateOnly executionDate,
        CancellationToken cancellationToken = default);

    Task ConfirmDownloadAsync(Guid orgId, Guid batchId, CancellationToken cancellationToken = default);

    Task ConfirmPaidAsync(Guid orgId, Guid batchId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Get the batch file content and mark it as downloaded on first retrieval. The file of a batch that is not paid yet is not handed out while the batch pays a
    /// payout reversed after it was made (<see cref="WireTransferErrors.BatchPayoutsReversed"/>); a paid batch stays readable.
    /// </summary>
    /// <param name="orgId">The caller's org ID (from tenant context).</param>
    /// <param name="batchId">The batch ID.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The batch and its XML file content.</returns>
    Task<(WireBatch Batch, string XmlContent)> GetBatchFileAsync(
        Guid orgId,
        Guid batchId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<WireBatchSummary>> ListBatchesAsync(Guid orgId, CancellationToken cancellationToken = default);

    /// <summary>
    /// The payouts of the org that a wire file of <paramref name="currency"/> can still pay (EUR for the SEPA file, CHF for the Swiss one; the case and the spaces of the
    /// code do not matter): the payouts <see cref="CreateBatchAsync"/> accepts, that is confirmed (never reversed), of a statement version published to the owner, with a
    /// net amount above zero and in no batch. One currency per list: the payouts of the other one are never in it. Oldest first, the order of the transfers of a batch, at
    /// most <see cref="WireTransferErrors.ListedPayablePayouts"/>. Each one comes with its owner, the period of its statement and whether the owner has an IBAN, as a yes or a
    /// no: <b>never an IBAN</b>, never an address. 422 <see cref="WireTransferErrors.CurrencyNotSupported"/> for a currency that has no wire file, or none: the list of «every
    /// currency» does not exist.
    /// </summary>
    Task<IReadOnlyList<WirePayoutSummary>> ListPayablePayoutsAsync(Guid orgId, string? currency, CancellationToken cancellationToken = default);

    Task<WireBatchPayoutsSummary> GetBatchPayoutsAsync(Guid orgId, Guid batchId, CancellationToken cancellationToken = default);
}

public sealed record WireBatchSummary(
    Guid Id,
    string Currency,
    WireBatchStatus Status,
    int TransferCount,
    decimal TotalAmount,
    DateOnly? ExecutionDate,
    string? MessageFormat,
    DateTime CreatedAt,
    DateTime? DownloadedAt,
    DateTime? PaidAt);

public sealed record WirePayoutSummary(
    Guid PayoutId,
    Guid OwnerId,
    string OwnerName,
    int PeriodYear,
    int PeriodMonth,
    decimal Amount,
    string Currency,
    DateTime ConfirmedAt,
    OwnerPayoutStatus Status,
    bool OwnerHasIban);

public sealed record WireBatchPayoutsSummary(WireBatchSummary Batch, IReadOnlyList<WirePayoutSummary> Payouts);

public static class WireMessageFormats
{
    public const string Pain001V09Ch03 = "pain.001.001.09.ch.03";

    public const string Pain001V09Sepa = "pain.001.001.09";

    public const string Pain001V03Sepa = "pain.001.001.03";

    public static string? Of(string? currency) => (currency ?? string.Empty).Trim().ToUpperInvariant() switch
    {
        WireTransferErrors.SwissCurrency => Pain001V09Ch03,
        WireTransferErrors.SepaCurrency => Pain001V09Sepa,
        _ => null,
    };
}

/// <summary>
/// The stable codes of the refusals of <see cref="IWireTransferService.CreateBatchAsync"/> and of the endpoints of the wires (E06/F05/US01/T01, E16/F01/US01/T01,
/// NF-25, NF-113), with the keys of their messages in <c>SharedResources</c> (the key is the code in Pascal case: <c>WireBatchXmlInvalid</c>, unless it is
/// said otherwise). Every one is 422 and none carries an IBAN or a name: the batch is not saved. The refusals about owners list their ids in the
/// <c>ownerIds</c> member of the problem, never their names.
/// </summary>
public static class WireTransferErrors
{
    public const string XmlInvalid = "wire_batch_xml_invalid";

    public const string SchemaUnavailable = "wire_batch_schema_unavailable";

    public const string ContentInvalid = "wire_batch_content_invalid";

    public const string CurrencyNotSupported = "wire_currency_not_supported";

    public const string AmountInvalid = "wire_amount_invalid";

    public const string FromStatementRetired = "wire_batch_from_statement_retired";

    public const string NoPayoutsProvided = "no_payouts_provided";

    public const string BatchTooLarge = "wire_batch_too_large";

    public const string PayoutsNotFound = "payouts_not_found_or_not_owned";

    public const string PayoutsNotConfirmed = "wire_payouts_not_confirmed";

    public const string PayoutsNotPublished = "wire_payouts_not_published";

    public const string PayoutsAlreadyInBatch = "payouts_already_in_batch";

    public const string BatchPayoutsReversed = "wire_batch_payouts_reversed";

    public const string MixedCurrencies = "mixed_currencies_not_allowed";

    public const string ExecutionDateInvalid = "wire_execution_date_invalid";

    public const string DebtorIncomplete = "wire_debtor_incomplete";

    public const string DebitAccountMissing = "wire_debit_account_missing";

    public const string DebitAccountInvalid = "invalid_debit_account_iban";

    public const string DebitIbanNotSwiss = "wire_debit_iban_not_swiss";

    public const string OwnerAddressMissing = "wire_owner_address_missing";

    public const string OwnerIbanMissing = "wire_owner_iban_missing";

    public const string OwnerIbanInvalid = "owner_invalid_iban";

    public const string OwnerQrIban = "wire_owner_qr_iban";

    public const string OwnerIbanNotSwiss = "wire_owner_iban_not_swiss";

    public const string OwnerIbanNotSepa = "wire_owner_iban_not_sepa";

    public const int ListedErrors = 5;

    public const int MaxTransfers = 1000;

    public const int ListedBatches = 200;

    public const int ListedPayablePayouts = MaxTransfers;

    public const string SepaCurrency = "EUR";

    public const string SwissCurrency = "CHF";
}
