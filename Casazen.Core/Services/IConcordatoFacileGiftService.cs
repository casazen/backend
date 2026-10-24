namespace Casazen.Core.Services;

/// <summary>
/// Result of consuming a ConcordatoFacile gift (ADR-006-R5).
/// </summary>
public enum CfGiftConsumeResult
{
    /// <summary>Gift consumed successfully for this lease</summary>
    Success,

    /// <summary>Gift already consumed for the same lease (re-entry allowed, ADR-006-R6)</summary>
    AlreadyConsumedForThisLease,

    /// <summary>Gift already consumed for another lease (409 conflict)</summary>
    AlreadyConsumedForOtherLease
}

/// <summary>
/// Manages ConcordatoFacile gift granting and consumption (ADR-006).
/// Gifts are granted via webhook when a biennial subscription is created,
/// and consumed atomically for a lease with PostgreSQL advisory locks.
/// </summary>
public interface IConcordatoFacileGiftService
{
    /// <summary>
    /// Grants a ConcordatoFacile gift to an organization when a biennial subscription is created (ADR-006-R1).
    /// Idempotent: if a gift already exists for the org, this is a no-op.
    /// </summary>
    /// <param name="orgId">The organization ID</param>
    /// <param name="subscriptionId">The Stripe subscription ID that granted the gift</param>
    /// <param name="cancellationToken">Cancellation token</param>
    Task GrantGiftAsync(Guid orgId, string subscriptionId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Consumes an available gift for a lease (ADR-006-R4, ADR-006-R5).
    /// Acquires a PostgreSQL advisory lock for atomicity (ADR-006-R4).
    /// Re-entry for the same leaseId returns Success (ADR-006-R6).
    /// Records a LeaseEvent on consumption (ADR-006-R7).
    /// </summary>
    /// <param name="orgId">The organization ID</param>
    /// <param name="leaseId">The lease contract ID to consume the gift for</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Success if consumed, AlreadyConsumedForThisLease if re-entry, AlreadyConsumedForOtherLease if unavailable</returns>
    Task<CfGiftConsumeResult> ConsumeGiftAsync(Guid orgId, Guid leaseId, CancellationToken cancellationToken = default);
}
