namespace Casazen.Web.Infrastructure;

/// <summary>
/// <c>code</c> values of plan-change errors (#274), raised by <c>PUT /api/orgs/me/plan</c> and
/// <c>PATCH /api/admin/orgs/{id}/plan</c>. Stable: the frontend translates them (<c>apiErrors.codes.*</c>).
/// </summary>
public static class PlanProblemCodes
{
    /// <summary>A live Stripe subscription drives the plan: change it from the billing portal.</summary>
    public const string ManagedByStripe = "managed_by_stripe";

    /// <summary>A paid tier needs an active subscription (Stripe Checkout).</summary>
    public const string SubscriptionRequired = "subscription_required";
}
