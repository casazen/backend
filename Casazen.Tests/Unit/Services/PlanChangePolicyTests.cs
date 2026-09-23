using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>#274: paid tiers only through a Stripe subscription; manual changes never overwrite a Stripe-managed plan.</summary>
public class PlanChangePolicyTests
{
    private static OrgEntity Org(PlanTier tier, SubscriptionStatus status, string? subscriptionId) => new()
    {
        Name = "Org",
        Slug = "org",
        DisplayName = "Org",
        ContactEmail = "o@x.it",
        PlanTier = tier,
        SubscriptionStatus = status,
        SubscriptionId = subscriptionId,
    };

    [Theory]
    [InlineData(PlanTier.Pro)]
    [InlineData(PlanTier.Scale)]
    public void EvaluateManualChange_UpgradeWithoutSubscription_ReturnsSubscriptionRequired(PlanTier requested)
    {
        var org = Org(PlanTier.Starter, SubscriptionStatus.None, null);

        var outcome = PlanChangePolicy.EvaluateManualChange(org, PlanTier.Starter, requested);

        Assert.Equal(ManualPlanChangeOutcome.SubscriptionRequired, outcome);
    }

    [Fact]
    public void EvaluateManualChange_StoredScaleWithoutSubscriptionToPro_ReturnsSubscriptionRequired()
    {
        // Lower than the stored tier but above the effective one (Starter): still an upgrade.
        var org = Org(PlanTier.Scale, SubscriptionStatus.None, null);

        var outcome = PlanChangePolicy.EvaluateManualChange(org, PlanTier.Starter, PlanTier.Pro);

        Assert.Equal(ManualPlanChangeOutcome.SubscriptionRequired, outcome);
    }

    [Theory]
    [InlineData(SubscriptionStatus.None, null)]
    [InlineData(SubscriptionStatus.Canceled, "sub_old")]
    [InlineData(SubscriptionStatus.None, "sub_incomplete")]
    public void EvaluateManualChange_BackToStarterWithoutActiveSubscription_ReturnsAllowed(
        SubscriptionStatus status,
        string? subscriptionId)
    {
        var org = Org(PlanTier.Pro, status, subscriptionId);

        var outcome = PlanChangePolicy.EvaluateManualChange(org, PlanTier.Starter, PlanTier.Starter);

        Assert.Equal(ManualPlanChangeOutcome.Allowed, outcome);
    }

    [Theory]
    [InlineData(SubscriptionStatus.Active)]
    [InlineData(SubscriptionStatus.Trialing)]
    [InlineData(SubscriptionStatus.PastDue)]
    public void EvaluateManualChange_ActiveSubscription_ReturnsManagedByStripe(SubscriptionStatus status)
    {
        var org = Org(PlanTier.Pro, status, "sub_live");

        Assert.Equal(
            ManualPlanChangeOutcome.ManagedByStripe,
            PlanChangePolicy.EvaluateManualChange(org, PlanTier.Pro, PlanTier.Starter));
        Assert.Equal(
            ManualPlanChangeOutcome.ManagedByStripe,
            PlanChangePolicy.EvaluateManualChange(org, PlanTier.Pro, PlanTier.Scale));
    }

    [Fact]
    public void HasActiveSubscription_ActiveStatusWithoutSubscriptionId_ReturnsFalse()
    {
        Assert.False(PlanChangePolicy.HasActiveSubscription(Org(PlanTier.Pro, SubscriptionStatus.Active, null)));
    }
}
