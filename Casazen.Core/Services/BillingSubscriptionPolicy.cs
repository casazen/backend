using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;

namespace Casazen.Core.Services;

/// <summary>
/// Rules shared by the Stripe billing webhook and the checkout (A1-10, A1-11): how a Stripe subscription status
/// is stored, and which subscriptions forbid a new checkout because it would create a second subscription.
/// Paid access itself is decided by <see cref="IEntitlementService.ResolveEffectiveTier"/>.
/// </summary>
public static class BillingSubscriptionPolicy
{
    /// <summary>Problem <c>code</c> of a checkout refused because the org already has a subscription (409).</summary>
    public const string AlreadySubscribedCode = "already_subscribed";

    /// <summary><c>SharedResources</c> key of the <see cref="AlreadySubscribedCode"/> message.</summary>
    public const string AlreadySubscribedMessageKey = "AlreadySubscribed";

    /// <summary>
    /// Stored status of a Stripe subscription status. <c>incomplete</c> and <c>unpaid</c> keep their own value
    /// (no paid access, but the subscription still exists on Stripe); <c>canceled</c> and the terminal
    /// <c>incomplete_expired</c> are <see cref="SubscriptionStatus.Canceled"/>; <c>paused</c> and unknown values are
    /// <see cref="SubscriptionStatus.None"/>. Everything but active, trialing and past due is fail-closed.
    /// </summary>
    public static SubscriptionStatus MapStripeStatus(string? stripeStatus) => stripeStatus switch
    {
        "trialing" => SubscriptionStatus.Trialing,
        "active" => SubscriptionStatus.Active,
        "past_due" => SubscriptionStatus.PastDue,
        "unpaid" => SubscriptionStatus.Unpaid,
        "incomplete" => SubscriptionStatus.Incomplete,
        "canceled" or "incomplete_expired" => SubscriptionStatus.Canceled,
        _ => SubscriptionStatus.None,
    };

    /// <summary>
    /// <c>true</c> when the org's stored subscription still exists on Stripe (active, trialing, past due, unpaid or
    /// waiting for its first payment): a new checkout would create a second subscription and charge twice.
    /// The org manages or pays it from the billing portal instead.
    /// </summary>
    public static bool BlocksNewCheckout(Org org) =>
        !string.IsNullOrWhiteSpace(org.SubscriptionId) &&
        org.SubscriptionStatus is SubscriptionStatus.Active
            or SubscriptionStatus.Trialing
            or SubscriptionStatus.PastDue
            or SubscriptionStatus.Unpaid
            or SubscriptionStatus.Incomplete;

    /// <summary>
    /// <c>true</c> when a subscription read from Stripe forbids a new checkout: every status except the terminal
    /// <c>canceled</c> and <c>incomplete_expired</c> (unknown future statuses included, fail-closed).
    /// </summary>
    public static bool BlocksNewCheckout(string? stripeStatus) =>
        stripeStatus is not ("canceled" or "incomplete_expired");
}
