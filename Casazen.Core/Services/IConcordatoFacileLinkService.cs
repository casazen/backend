using System.Security.Claims;

namespace Casazen.Core.Services;

/// <summary>
/// Result of generating a signed link to the ConcordatoFacile partner portal.
/// </summary>
public sealed record CfLinkResult(string Url);

/// <summary>
/// Generates HMAC-SHA256 signed links to the ConcordatoFacile partner portal (ADR-007).
/// Links are time-limited (15 minutes) and include lease and municipality data.
/// </summary>
public interface IConcordatoFacileLinkService
{
    /// <summary>
    /// Generates a signed link to the ConcordatoFacile partner portal for the specified lease.
    /// Verifies access, loads municipality data, computes HMAC signature, and records a LeaseEvent.
    /// </summary>
    /// <param name="leaseId">The lease contract ID</param>
    /// <param name="user">The claims principal (for access check)</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The signed URL to open in a new tab</returns>
    /// <exception cref="UnauthorizedAccessException">User does not have ConcordatoFacile access for this lease</exception>
    /// <exception cref="Casazen.Core.Exceptions.DomainRuleException">Property municipality is unknown (code: property_municipality_unknown)</exception>
    Task<CfLinkResult> GenerateSignedLinkAsync(
        Guid leaseId,
        ClaimsPrincipal user,
        CancellationToken cancellationToken = default);
}
