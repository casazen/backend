using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;

namespace Casazen.Core.Services;

/// <summary>A Stripe Checkout Session in subscription mode, with the plan tier stored in its metadata.</summary>
public sealed record StripeCheckoutSession(string Id, string Url, PlanTier? PlanTier);

/// <summary>A Stripe subscription of a customer, with its raw Stripe status (<c>active</c>, <c>incomplete</c>…).</summary>
public sealed record StripeSubscriptionSummary(string Id, string Status);

/// <summary>
/// Stripe calls of the platform billing (plan subscriptions). The checkout rules live in
/// <see cref="IBillingCheckoutService"/>; this gateway only talks to Stripe.
/// </summary>
public interface IStripeBillingService
{
    Task<string> EnsureCustomerAsync(Org org, CancellationToken cancellationToken = default);

    Task<StripeCheckoutSession> CreateCheckoutSessionAsync(Org org, PlanTier planTier, string successUrl, string cancelUrl, CancellationToken cancellationToken = default);

    /// <summary>Open (not completed, not expired) subscription-mode Checkout Sessions of the customer.</summary>
    Task<IReadOnlyList<StripeCheckoutSession>> ListOpenCheckoutSessionsAsync(string customerId, CancellationToken cancellationToken = default);

    /// <summary>Expires an open Checkout Session: the customer can no longer complete it.</summary>
    Task ExpireCheckoutSessionAsync(string sessionId, CancellationToken cancellationToken = default);

    /// <summary>Subscriptions of the customer in every status, read from Stripe (the source of truth).</summary>
    Task<IReadOnlyList<StripeSubscriptionSummary>> ListSubscriptionsAsync(string customerId, CancellationToken cancellationToken = default);

    Task<string> CreatePortalSessionAsync(Org org, CancellationToken cancellationToken = default);

    PlanTier? MapPriceIdToTier(string? priceId);
}
