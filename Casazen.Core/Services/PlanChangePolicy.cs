using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;

namespace Casazen.Core.Services;

/// <summary>Outcome of a plan change requested outside Stripe (see <see cref="PlanChangePolicy"/>).</summary>
public enum ManualPlanChangeOutcome
{
    Allowed,

    /// <summary>A live Stripe subscription drives the plan: it changes only through Stripe (portal and webhooks).</summary>
    ManagedByStripe,

    /// <summary>The requested tier is above the effective one and no subscription pays for it.</summary>
    SubscriptionRequired,
}

/// <summary>
/// Paid tiers are granted only by a Stripe subscription (#274). A manual plan change
/// (<c>PUT /api/orgs/me/plan</c>, <c>PATCH /api/admin/orgs/{id}/plan</c>) never overwrites a plan that Stripe
/// manages and, without a subscription, may only move to a tier not above the effective one
/// (a downgrade or back to Starter).
/// </summary>
public static class PlanChangePolicy
{
    /// <summary>
    /// <c>true</c> when a Stripe subscription currently drives the org's plan (active, trialing or past due).
    /// Canceled, incomplete, unpaid and unknown Stripe states do not count: they give no paid access, so only a
    /// move to Starter is possible and the next webhook sets the tier again.
    /// </summary>
    public static bool HasActiveSubscription(Org org) =>
        !string.IsNullOrWhiteSpace(org.SubscriptionId) &&
        org.SubscriptionStatus is SubscriptionStatus.Active or SubscriptionStatus.Trialing or SubscriptionStatus.PastDue;

    /// <param name="org">The org whose plan would change.</param>
    /// <param name="effectiveTier">The org's effective tier (<see cref="IEntitlementService.ResolveEffectiveTier"/>).</param>
    /// <param name="requestedTier">The tier requested by the caller.</param>
    public static ManualPlanChangeOutcome EvaluateManualChange(Org org, PlanTier effectiveTier, PlanTier requestedTier)
    {
        if (HasActiveSubscription(org))
            return ManualPlanChangeOutcome.ManagedByStripe;

        return PlanCatalog.Rank(requestedTier) > PlanCatalog.Rank(effectiveTier)
            ? ManualPlanChangeOutcome.SubscriptionRequired
            : ManualPlanChangeOutcome.Allowed;
    }
}
