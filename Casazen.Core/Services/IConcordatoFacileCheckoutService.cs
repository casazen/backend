namespace Casazen.Core.Services;

/// <summary>
/// Result of creating a ConcordatoFacile checkout session.
/// </summary>
public sealed record CheckoutResult(string SessionId, string SessionUrl, bool Reused);

/// <summary>
/// Creates Stripe Checkout sessions for ConcordatoFacile one-time payments (ADR-005).
/// </summary>
public interface IConcordatoFacileCheckoutService
{
    /// <summary>
    /// Creates or reuses a Stripe Checkout session for ConcordatoFacile access purchase.
    /// Reuses an existing pending session for the same (orgId, leaseId) if not expired.
    /// Throws <see cref="Exceptions.NotFoundException"/> (<c>lease_not_found</c>) for a lease that does not exist or is not the
    /// org's, and <see cref="Exceptions.DomainConflictException"/> (<c>concordatofacile_already_purchased</c>) when the lease
    /// already has the access (completed purchase, consumed gift) or has paid and the webhook has not arrived: a second payment is
    /// never offered. A paid session whose webhook is missing completes its purchase before the exception is thrown.
    /// </summary>
    Task<CheckoutResult> CreateCheckoutSessionAsync(
        Guid leaseId,
        Guid orgId,
        string userId,
        CancellationToken cancellationToken = default);
}
