using System.Security.Claims;

namespace Casazen.Core.Services;

/// <summary>
/// Result of checking ConcordatoFacile access for a lease.
/// </summary>
public sealed record CfAccessResult(
    bool HasAccess,
    CfAccessSource Source,
    bool GiftAvailable,
    bool GiftConsumed,
    DateTime? PurchasedAt,
    DateTime? GiftAssignedAt);

/// <summary>
/// Source of ConcordatoFacile access (ADR-005-R8).
/// </summary>
public enum CfAccessSource
{
    /// <summary>No access granted</summary>
    None = 0,

    /// <summary>Access granted via completed payment</summary>
    Payment = 1,

    /// <summary>Access granted via consumed gift</summary>
    Gift = 2,

    /// <summary>Access granted via platform admin role</summary>
    Admin = 3
}

/// <summary>
/// Checks ConcordatoFacile access for a lease (ADR-005-R8).
/// Access is granted if the lease has a completed purchase, a consumed gift, or the user is a platform admin.
/// </summary>
public interface IConcordatoFacileAccessService
{
    /// <summary>
    /// Checks if the user has access to ConcordatoFacile for the specified lease.
    /// Checks server-side state only (payment, gift, admin role), never client state.
    /// </summary>
    /// <param name="leaseId">The lease contract ID to check access for</param>
    /// <param name="user">The claims principal (for admin role check)</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Access result with source, gift availability, and timestamps</returns>
    Task<CfAccessResult> CheckAccessAsync(
        Guid leaseId,
        ClaimsPrincipal user,
        CancellationToken cancellationToken = default);
}
