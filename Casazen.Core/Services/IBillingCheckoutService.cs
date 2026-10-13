using Casazen.Core.Entities.Enums;

namespace Casazen.Core.Services;

/// <summary>
/// Starts the Stripe Checkout of a paid plan without ever creating a second subscription for the same org (A1-10).
/// </summary>
public interface IBillingCheckoutService
{
    /// <summary>
    /// Returns the URL of a Checkout Session for <paramref name="planTier"/>. One checkout runs at a time per org;
    /// an open session for the same tier is reused (double click, second tab) and open sessions for another tier
    /// are expired, so only one can be paid.
    /// </summary>
    /// <exception cref="Casazen.Core.Exceptions.DomainConflictException">
    /// <c>already_subscribed</c> when the org has a subscription, stored or already on Stripe but not yet
    /// received through the webhook: it is managed from the billing portal.
    /// </exception>
    /// <exception cref="Casazen.Core.Exceptions.NotFoundException">The org does not exist.</exception>
    Task<string> StartCheckoutAsync(
        Guid orgId,
        PlanTier planTier,
        string successUrl,
        string cancelUrl,
        CancellationToken cancellationToken = default);
}
