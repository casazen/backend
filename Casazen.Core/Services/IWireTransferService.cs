using Casazen.Core.Entities;

namespace Casazen.Core.Services;

/// <summary>
/// Wire transfer service (ADR-017): generates SEPA pain.001 (Italy) or Swiss Payment Standards (Switzerland) XML files
/// for paying property owners. One wire per owner (aggregating multiple statements), one file per currency and debit account.
/// IBANs are validated with check digits. Files are validated against official ISO 20022 XSD. Each statement appears in exactly one batch.
/// </summary>
public interface IWireTransferService
{
    /// <summary>
    /// Create a wire batch from the given statement IDs. Validates that all statements belong to the caller's org,
    /// are Published, in the same currency, and not already in a batch. Returns the batch ID and the XML file content.
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
}
