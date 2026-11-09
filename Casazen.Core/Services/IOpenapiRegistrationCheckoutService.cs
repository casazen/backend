namespace Casazen.Core.Services;

/// <summary>
/// Stripe Checkout for the Openapi filing fee of a signed lease, including canone concordato.
/// Same session shape as the ConcordatoFacile checkout (<see cref="CheckoutResult"/>).
/// </summary>
public interface IOpenapiRegistrationCheckoutService
{
    Task<CheckoutResult> CreateCheckoutSessionAsync(
        Guid leaseId,
        Guid orgId,
        string userId,
        CancellationToken cancellationToken = default);
}
