using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;

namespace Casazen.Core.Services;

/// <summary>A Stripe Checkout Session in subscription mode, with the plan tier stored in its metadata.</summary>
public sealed record StripeCheckoutSession(string Id, string Url, PlanTier? PlanTier);

/// <summary>A Stripe subscription of a customer, with its raw Stripe status (<c>active</c>, <c>incomplete</c>…).</summary>
public sealed record StripeSubscriptionSummary(string Id, string Status);

/// <summary>
/// A Stripe tax rate used by Stripe Tax on an invoice (PL-13): country and effective percentage as Stripe returns them.
/// </summary>
public sealed record StripeTaxRateSummary(string Id, string? Country, decimal? EffectivePercentage, string? Jurisdiction);

/// <summary>A tax id of a Stripe customer with its VIES verification status (<c>pending</c>, <c>verified</c>, ...).</summary>
public sealed record StripeCustomerTaxId(string Type, string Value, string? VerificationStatus);

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

    /// <summary>
    /// Billing portal session of the org's Stripe customer. <paramref name="returnUrl"/> is the page of the public web
    /// app the portal links back to (built from <c>App:PublicSiteBaseUrl</c>, PL-11).
    /// </summary>
    Task<string> CreatePortalSessionAsync(Org org, string returnUrl, CancellationToken cancellationToken = default);

    /// <summary>Tax rates by id (Stripe Tax creates them for automatic tax), used to record a paid invoice (PL-13).</summary>
    Task<IReadOnlyList<StripeTaxRateSummary>> GetTaxRatesAsync(IReadOnlyCollection<string> taxRateIds, CancellationToken cancellationToken = default);

    /// <summary>Tax ids of the customer, with the VIES verification status Stripe computed (PL-13).</summary>
    Task<IReadOnlyList<StripeCustomerTaxId>> ListCustomerTaxIdsAsync(string customerId, CancellationToken cancellationToken = default);

    /// <summary>Plan tier whose configured Stripe Price id (<c>Billing:Prices:&lt;Tier&gt;</c>) is <paramref name="priceId"/>.</summary>
    PlanTier? MapPriceIdToTier(string? priceId);
}
