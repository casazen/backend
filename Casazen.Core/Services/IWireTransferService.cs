using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;

namespace Casazen.Core.Services;

/// <summary>
/// Wire transfer service (ADR-017, E16/F01/US01/T01): builds the bank file that pays the owners from their <b>confirmed payouts</b> (the net after the withholding,
/// <see cref="OwnerPayout"/>), never from a statement. CHF is the Swiss <c>pain.001.001.09.ch.03</c> file, EUR the SEPA <c>pain.001.001.03</c> file (until its own
/// change). One transfer for each payout, one file per currency, the debtor and the debit account those of the issuer profile of the org. A payout is paid by
/// one batch only. IBANs are validated with check digits. A file that does not validate against the official schema of its format is never saved, and a file is
/// never produced without its validation (<see cref="WireTransferErrors"/>).
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
    /// the issuer profile of the org has its name, structured address and a valid IBAN; that every owner has a valid IBAN (never a QR-IBAN) and a structured address;
    /// and that the file validates against the official schema of its format: all before anything is saved (<see cref="WireTransferErrors"/>). The batch, its
    /// payouts and its file are written by one save. Returns the batch and the XML file content.
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

    /// <summary>
    /// Mark a batch of the org as downloaded. Updates Status to Downloaded and sets DownloadedAt. A batch of another org is not found.
    /// </summary>
    Task ConfirmDownloadAsync(Guid orgId, Guid batchId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Mark a batch of the org as paid. Updates Status to Paid and sets PaidAt. A batch of another org is not found.
    /// </summary>
    Task ConfirmPaidAsync(Guid orgId, Guid batchId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Get the batch file content and mark it as downloaded on first retrieval.
    /// </summary>
    /// <param name="orgId">The caller's org ID (from tenant context).</param>
    /// <param name="batchId">The batch ID.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The batch and its XML file content.</returns>
    Task<(WireBatch Batch, string XmlContent)> GetBatchFileAsync(
        Guid orgId,
        Guid batchId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// The batches of the org, newest first (at most <see cref="WireTransferErrors.ListedBatches"/>): state, currency, number of transfers and total, with no IBAN,
    /// no name and no file.
    /// </summary>
    Task<IReadOnlyList<WireBatchSummary>> ListBatchesAsync(Guid orgId, CancellationToken cancellationToken = default);
}

/// <summary>
/// A batch as the list shows it: the state, the currency, how many transfers and how much, when it was made, downloaded and paid, and the message and the date it
/// asks of the bank. <b>No IBAN, no name and no file</b>: the type has no member that could carry one.
/// </summary>
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

/// <summary>The messages a wire batch can be (<see cref="WireBatch.MessageFormat"/>).</summary>
public static class WireMessageFormats
{
    /// <summary>The Swiss credit transfer of the Swiss Payment Standards, ISO 20022 <c>pain.001.001.09</c> with the Swiss implementation guidelines (CHF).</summary>
    public const string Pain001V09Ch03 = "pain.001.001.09.ch.03";

    /// <summary>The ISO 20022 <c>pain.001.001.03</c> SEPA credit transfer (EUR, until the change that moves it to <c>pain.001.001.09</c>).</summary>
    public const string Pain001V03Sepa = "pain.001.001.03";

    /// <summary>The message of the file of <paramref name="currency"/>, or <c>null</c> for a currency that has no wire file.</summary>
    public static string? Of(string? currency) => (currency ?? string.Empty).Trim().ToUpperInvariant() switch
    {
        WireTransferErrors.SwissCurrency => Pain001V09Ch03,
        WireTransferErrors.SepaCurrency => Pain001V03Sepa,
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
    /// <summary>The file does not validate against the official schema of its currency.</summary>
    public const string XmlInvalid = "wire_batch_xml_invalid";

    /// <summary>The official schema of the currency is not installed in this build: no file can be validated, so none is produced.</summary>
    public const string SchemaUnavailable = "wire_batch_schema_unavailable";

    /// <summary>The file validates but is not what its lines say: the number of operations, the control sum, an amount, a currency or an end-to-end id.</summary>
    public const string ContentInvalid = "wire_batch_content_invalid";

    /// <summary>The currency of the payouts is not one of the wire files (EUR for the SEPA file, CHF for the Swiss one).</summary>
    public const string CurrencyNotSupported = "wire_currency_not_supported";

    /// <summary>The amount of a transfer is zero, negative or has more than two decimals.</summary>
    public const string AmountInvalid = "wire_amount_invalid";

    /// <summary>The request is the one of before the payouts: it asks for a batch of statements and names a debit account. Refused, nothing is saved.</summary>
    public const string FromStatementRetired = "wire_batch_from_statement_retired";

    /// <summary>The request names no payout. An empty list is never «all the payouts».</summary>
    public const string NoPayoutsProvided = "no_payouts_provided";

    /// <summary>The request names more payouts than a batch holds (<see cref="MaxTransfers"/>).</summary>
    public const string BatchTooLarge = "wire_batch_too_large";

    /// <summary>A payout does not exist in the org (or belongs to another one).</summary>
    public const string PayoutsNotFound = "payouts_not_found_or_not_owned";

    /// <summary>A payout is not confirmed: it was reversed.</summary>
    public const string PayoutsNotConfirmed = "wire_payouts_not_confirmed";

    /// <summary>A payout is of a statement version that was never published to the owner.</summary>
    public const string PayoutsNotPublished = "wire_payouts_not_published";

    /// <summary>A payout is already paid by a batch.</summary>
    public const string PayoutsAlreadyInBatch = "payouts_already_in_batch";

    /// <summary>The payouts are in more than one currency: a batch is one currency.</summary>
    public const string MixedCurrencies = "mixed_currencies_not_allowed";

    /// <summary>The execution date is missing, before today or more than a year ahead.</summary>
    public const string ExecutionDateInvalid = "wire_execution_date_invalid";

    /// <summary>The issuer profile of the org has no legal name or no complete structured address: the debtor of the file cannot be written.</summary>
    public const string DebtorIncomplete = "wire_debtor_incomplete";

    /// <summary>The issuer profile of the org has no IBAN: there is no account to debit.</summary>
    public const string DebitAccountMissing = "wire_debit_account_missing";

    /// <summary>The IBAN of the issuer profile does not pass the check digits.</summary>
    public const string DebitAccountInvalid = "invalid_debit_account_iban";

    /// <summary>An owner has no complete structured address (street, postal code, town and country): the creditor of the file cannot be written. Lists the owner ids.</summary>
    public const string OwnerAddressMissing = "wire_owner_address_missing";

    /// <summary>An owner has no IBAN. Lists the owner ids.</summary>
    public const string OwnerIbanMissing = "wire_owner_iban_missing";

    /// <summary>An owner has an IBAN that does not pass the check digits. Lists the owner ids.</summary>
    public const string OwnerIbanInvalid = "owner_invalid_iban";

    /// <summary>The IBAN of an owner is a QR-IBAN, which is the account of a QR-bill and never the creditor account of a normal transfer. Lists the owner ids.</summary>
    public const string OwnerQrIban = "wire_owner_qr_iban";

    /// <summary>The IBAN of an owner is not Swiss or Liechtenstein, so a CHF transfer to it is not a domestic payment of this file. Lists the owner ids.</summary>
    public const string OwnerIbanNotSwiss = "wire_owner_iban_not_swiss";

    /// <summary>How many errors the message of <see cref="XmlInvalid"/> lists.</summary>
    public const int ListedErrors = 5;

    /// <summary>The most transfers a batch holds.</summary>
    public const int MaxTransfers = 1000;

    /// <summary>The most batches the list returns (the newest ones).</summary>
    public const int ListedBatches = 200;

    /// <summary>The currency of the SEPA file.</summary>
    public const string SepaCurrency = "EUR";

    /// <summary>The currency of the Swiss file.</summary>
    public const string SwissCurrency = "CHF";
}
