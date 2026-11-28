using System.Security.Claims;

namespace Casazen.Core.Services;

public sealed record CfAccessResult(
    bool HasAccess,
    CfAccessSource Source,
    bool GiftAvailable,
    bool GiftConsumed,
    DateTime? PurchasedAt,
    DateTime? GiftAssignedAt);

public enum CfAccessSource
{
    None = 0,

    Payment = 1,

    Gift = 2,

    Admin = 3
}

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
