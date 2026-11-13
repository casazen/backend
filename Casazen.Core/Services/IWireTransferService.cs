using Casazen.Core.Entities;

namespace Casazen.Core.Services;

/// <summary>
/// Wire transfer service (ADR-017): generates SEPA pain.001 (Italy) or Swiss Payment Standards (Switzerland) XML files
/// for paying property owners. One wire per owner (aggregating multiple statements), one file per currency and debit account.
/// IBANs are validated with check digits. Files are validated against official ISO 20022 XSD. Each statement appears in exactly one batch.
/// A file that does not validate is never saved, and a file is never produced without its validation (<see cref="WireTransferErrors"/>).
/// </summary>
public interface IWireTransferService
{
    /// <summary>
    /// Create a wire batch from the given statement IDs. Validates that all statements belong to the caller's org,
    /// are Published, in the same currency (EUR or CHF), and not already in a batch, that every owner has a valid IBAN and a positive amount,
    /// and that the file validates against the official schema of its currency: all before anything is saved (<see cref="WireTransferErrors"/>).
    /// Returns the batch ID and the XML file content.
    /// One wire transfer per owner (aggregating statements when the same owner has multiple statements in the input).
    /// </summary>
    /// <param name="orgId">The caller's org ID (from tenant context).</param>
    /// <param name="statementIds">IDs of the published owner statements to include in the batch.</param>
    /// <param name="debitAccount">The IBAN from which the transfers will be sent.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The created batch and the XML file content (SEPA pain.001 or Swiss format).</returns>
    Task<(WireBatch Batch, string XmlContent)> CreateBatchAsync(
        Guid orgId,
        List<Guid> statementIds,
        string debitAccount,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Mark a batch as downloaded. Updates Status to Downloaded and sets DownloadedAt.
    /// </summary>
    Task ConfirmDownloadAsync(Guid batchId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Mark a batch as paid. Updates Status to Paid and sets PaidAt.
    /// </summary>
    Task ConfirmPaidAsync(Guid batchId, CancellationToken cancellationToken = default);

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
}

/// <summary>
/// The stable codes of the refusals of <see cref="IWireTransferService.CreateBatchAsync"/> that are new with the validation of the file (E06/F05/US01/T01,
/// NF-25), with the keys of their messages in <c>SharedResources</c> (the key is the code in Pascal case: <c>WireBatchXmlInvalid</c>). Every one is
/// 422 and none carries an IBAN or a name: the batch is not saved.
/// </summary>
public static class WireTransferErrors
{
    /// <summary>The file does not validate against the official schema of its currency.</summary>
    public const string XmlInvalid = "wire_batch_xml_invalid";

    /// <summary>The official schema of the currency is not installed in this build: no file can be validated, so none is produced.</summary>
    public const string SchemaUnavailable = "wire_batch_schema_unavailable";

    /// <summary>The file validates but is not what its lines say: the number of operations, the control sum, an amount, a currency or an end-to-end id.</summary>
    public const string ContentInvalid = "wire_batch_content_invalid";

    /// <summary>The currency of the statements is not one of the wire files (EUR for the SEPA file, CHF for the Swiss one).</summary>
    public const string CurrencyNotSupported = "wire_currency_not_supported";

    /// <summary>The amount of a transfer is zero, negative or has more than two decimals.</summary>
    public const string AmountInvalid = "wire_amount_invalid";

    /// <summary>How many errors the message of <see cref="XmlInvalid"/> lists.</summary>
    public const int ListedErrors = 5;

    /// <summary>The currency of the SEPA file.</summary>
    public const string SepaCurrency = "EUR";

    /// <summary>The currency of the Swiss file.</summary>
    public const string SwissCurrency = "CHF";
}
