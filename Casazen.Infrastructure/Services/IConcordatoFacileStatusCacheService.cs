using Casazen.Infrastructure.External;

namespace Casazen.Infrastructure.Services;

/// <summary>
/// Cached practice status retrieval for ConcordatoFacile with 15-minute expiry and request coalescing (ADR-008-R7 through ADR-008-R10).
/// </summary>
public interface IConcordatoFacileStatusCacheService
{
    /// <summary>
    /// Get practices for a lease, from cache if valid or by calling the CF API. The lease must belong to the organization: a lease
    /// that does not exist or is another org's is a <see cref="Casazen.Core.Exceptions.NotFoundException"/> (<c>lease_not_found</c>)
    /// raised before the cache is read and before ConcordatoFacile is asked (ADR-008-R11).
    /// It does not fail when the status cannot be read (CF-BE): with the status API key not configured, or with ConcordatoFacile failing
    /// and no snapshot to fall back on, the result has <see cref="CfStatusResult.IsAvailable"/> false, no practices and the outcome is
    /// remembered for five minutes, during which ConcordatoFacile is not asked again.
    /// </summary>
    /// <param name="leaseId">The lease contract ID.</param>
    /// <param name="orgId">The organization ID (for tenant isolation, ADR-008-R10).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Cached or fresh practice status with metadata.</returns>
    Task<CfStatusResult> GetPracticesAsync(Guid leaseId, Guid orgId, CancellationToken cancellationToken = default);

    /// <summary>
    /// A practice by its number, from the memory cache (15 minutes per environment and practice) or from the CF API; null when
    /// ConcordatoFacile does not know it. It is ConcordatoFacile's own data, the same for every org: the caller checks that the
    /// lease it names is the caller's org's.
    /// </summary>
    /// <param name="practiceId">The practice number (<c>P-</c> and 16 hexadecimal characters), already validated.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<PracticeDto?> GetPracticeAsync(string practiceId, CancellationToken cancellationToken = default);
}

/// <summary>
/// Result of a cached or fresh practice status fetch.
/// </summary>
public sealed record CfStatusResult
{
    public required IReadOnlyList<PracticeDto> Practices { get; init; }

    /// <summary>When the practices were last read successfully; null when they never were (CF-BE: not available).</summary>
    public required DateTime? LastCheckedAt { get; init; }
    public required DateTime NextUpdateAt { get; init; }
    public required bool IsStale { get; init; }

    /// <summary>True when the practices come from ConcordatoFacile (a successful read or a valid snapshot, stale or not); false when the status could not be read and no snapshot exists (CF-BE).</summary>
    public bool IsAvailable { get; init; } = true;
    public string? LastError { get; init; }
}
