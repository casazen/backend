namespace Casazen.Infrastructure.External;

/// <summary>
/// Where the status cache reads the practices of ConcordatoFacile from (specification v0.2 §4): <see cref="ConcordatoFacileApiClient"/>,
/// the HTTP client of the real status API, or <c>SimulatedConcordatoFacileApiClient</c>, which reads the practices of the simulated
/// module and is registered instead of it only with <c>ConcordatoFacile:FormMode=Simulated</c>, in the test environment (CF-SIM).
/// </summary>
public interface IConcordatoFacileApiClient
{
    /// <summary>
    /// Whether the status can be read at all (CF-BE). The HTTP client of the real status API can only when the status API key is configured
    /// (without it ConcordatoFacile answers 401 to everything): the status cache then does not ask and answers "not available". The
    /// simulated client reads a table of the application and needs no key: always true.
    /// </summary>
    bool IsConfigured { get; }

    Task<List<PracticeDto>> GetPracticesByLeaseIdAsync(Guid leaseId, CancellationToken cancellationToken = default);

    Task<PracticeDto?> GetPracticeAsync(string practiceId, CancellationToken cancellationToken = default);
}
