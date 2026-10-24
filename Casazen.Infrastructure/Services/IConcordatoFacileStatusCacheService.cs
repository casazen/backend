using Casazen.Infrastructure.External;

namespace Casazen.Infrastructure.Services;

/// <summary>
/// Cached practice status retrieval for ConcordatoFacile with 15-minute expiry and request coalescing (ADR-008-R7 through ADR-008-R10).
/// </summary>
public interface IConcordatoFacileStatusCacheService
{
    /// <summary>
    /// Get practices for a lease, from cache if valid or by calling the CF API.
    /// </summary>
    /// <param name="leaseId">The lease contract ID.</param>
    /// <param name="orgId">The organization ID (for tenant isolation, ADR-008-R10).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Cached or fresh practice status with metadata.</returns>
    Task<CfStatusResult> GetPracticesAsync(Guid leaseId, Guid orgId, CancellationToken cancellationToken = default);
}

/// <summary>
/// Result of a cached or fresh practice status fetch.
/// </summary>
public sealed record CfStatusResult
{
    public required IReadOnlyList<PracticeDto> Practices { get; init; }
    public required DateTime LastCheckedAt { get; init; }
    public required DateTime NextUpdateAt { get; init; }
    public required bool IsStale { get; init; }
    public string? LastError { get; init; }
}
