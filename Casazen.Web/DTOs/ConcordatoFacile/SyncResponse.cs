namespace Casazen.Web.Dtos.ConcordatoFacile;

/// <summary>
/// Response from POST /api/leases/{leaseId}/concordatofacile/sync (CF-DONE): where the lease of the guided service stands after it was made
/// to follow its practice.
/// </summary>
public class SyncResponse
{
    /// <summary>
    /// What the lease waits for: <c>NotApplicable</c> (not a lease of the service), <c>WaitingForContract</c> (no contract document uploaded),
    /// <c>ReadingContract</c>, <c>WaitingForSignature</c> (the practice does not say yet that the contract is signed),
    /// <c>ContractNeedsData</c> (the person completes the import: data or the day of the signature the reading did not find),
    /// <c>WaitingForReceipt</c>, <c>ReadingReceipt</c>, <c>ReceiptNotUsable</c> (the person declares the registration), <c>Completed</c>.
    /// </summary>
    public required string State { get; init; }

    /// <summary>The status of the lease now (<c>Draft</c>, <c>Signed</c>, <c>Registered</c>…): it moves with the practice.</summary>
    public required string LeaseStatus { get; init; }
}
