namespace Casazen.Web.Dtos.ConcordatoFacile;

/// <summary>
/// Response from POST /api/leases/{leaseId}/concordatofacile/link (ADR-007).
/// Contains the time-limited signed URL to the ConcordatoFacile partner portal.
/// </summary>
public class LinkResponse
{
    /// <summary>
    /// The signed URL to open in a new tab (expires in 15 minutes).
    /// Includes partner, leaseId, expiresAt, signature, comune, and provincia parameters.
    /// </summary>
    public required string Url { get; init; }
}
