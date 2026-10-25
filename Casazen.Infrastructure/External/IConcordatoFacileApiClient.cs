namespace Casazen.Infrastructure.External;

/// <summary>
/// Where the status cache reads the practices of ConcordatoFacile from (specification v0.2 §4): <see cref="ConcordatoFacileApiClient"/>,
/// the HTTP client of the real status API, or <c>SimulatedConcordatoFacileApiClient</c>, which reads the practices of the simulated
/// module and is registered instead of it only with <c>ConcordatoFacile:FormMode=Simulated</c>, in the test environment (CF-SIM).
/// </summary>
public interface IConcordatoFacileApiClient
{
    /// <summary>
    /// The practices of a lease, newest first and at most 20 (<c>GET /v1/leases/{leaseId}/practices</c>); an empty list for a lease with
    /// no practice. Any other answer throws: the caller keeps its last valid snapshot as stale and must not take an error for an empty list.
    /// </summary>
    Task<List<PracticeDto>> GetPracticesByLeaseIdAsync(Guid leaseId, CancellationToken cancellationToken = default);

    /// <summary>A practice by its number (<c>GET /v1/practices/{practiceId}</c>), with its <c>leaseId</c>; null when it is not known.</summary>
    Task<PracticeDto?> GetPracticeAsync(string practiceId, CancellationToken cancellationToken = default);
}
