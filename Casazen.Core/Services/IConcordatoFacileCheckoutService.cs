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
    /// </summary>
    Task<CheckoutResult> CreateCheckoutSessionAsync(
        Guid leaseId,
        Guid orgId,
        string userId,
        CancellationToken cancellationToken = default);
}
