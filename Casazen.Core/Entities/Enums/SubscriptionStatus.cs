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

    /// <summary>A renewal payment failed: paid access only within <c>Billing:PastDueGraceDays</c>.</summary>
    PastDue,

    /// <summary>Stripe <c>canceled</c> or <c>incomplete_expired</c> (terminal): no paid access.</summary>
    Canceled,

    /// <summary>Stripe <c>incomplete</c>: the first payment has not succeeded yet (A1-11). No paid access.</summary>
    Incomplete,

    /// <summary>Stripe <c>unpaid</c>: retries exhausted, the subscription still exists (A1-11). No paid access.</summary>
    Unpaid,
}
