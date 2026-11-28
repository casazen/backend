namespace Casazen.Core.Entities.Enums;

/// <summary>
/// Platform subscription state of an org, mapped from the Stripe subscription status by
/// <c>BillingSubscriptionPolicy.MapStripeStatus</c>. Stored as an integer: append new values, never reorder.
/// Only <see cref="Active"/>, <see cref="Trialing"/> and <see cref="PastDue"/> within the grace period give
/// access to the paid plan (see <c>IEntitlementService.ResolveEffectiveTier</c>).
/// </summary>
public enum SubscriptionStatus
{
    /// <summary>Never subscribed, or a Stripe state the platform does not map (e.g. <c>paused</c>): no paid access.</summary>
    None,
    Trialing,
    Active,

    PastDue,

    Canceled,

    Incomplete,

    /// <summary>Stripe <c>unpaid</c>: retries exhausted, the subscription still exists (A1-11). No paid access.</summary>
    Unpaid,
}
