namespace Casazen.Core.Services;

public interface IOpenapiRegistrationCheckoutService
{
    Task<CheckoutResult> CreateCheckoutSessionAsync(
        Guid leaseId,
        Guid orgId,
        string userId,
        CancellationToken cancellationToken = default);
}
