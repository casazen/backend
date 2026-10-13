using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;
using Xunit;

namespace Casazen.Tests.Unit.Services;

public class BillingSubscriptionPolicyTests
{
    [Theory]
    [InlineData("trialing", SubscriptionStatus.Trialing)]
    [InlineData("active", SubscriptionStatus.Active)]
    [InlineData("past_due", SubscriptionStatus.PastDue)]
    [InlineData("unpaid", SubscriptionStatus.Unpaid)]
    [InlineData("incomplete", SubscriptionStatus.Incomplete)]
    [InlineData("incomplete_expired", SubscriptionStatus.Canceled)]
    [InlineData("canceled", SubscriptionStatus.Canceled)]
    [InlineData("paused", SubscriptionStatus.None)]
    [InlineData("some_future_status", SubscriptionStatus.None)]
    [InlineData(null, SubscriptionStatus.None)]
    public void MapStripeStatus_StripeStatus_MapsToStoredStatus(string? stripeStatus, SubscriptionStatus expected) =>
        Assert.Equal(expected, BillingSubscriptionPolicy.MapStripeStatus(stripeStatus));

    [Theory]
    [InlineData(SubscriptionStatus.Active, true)]
    [InlineData(SubscriptionStatus.Trialing, true)]
    [InlineData(SubscriptionStatus.PastDue, true)]
    [InlineData(SubscriptionStatus.Unpaid, true)]
    [InlineData(SubscriptionStatus.Incomplete, true)]
    [InlineData(SubscriptionStatus.Canceled, false)]
    [InlineData(SubscriptionStatus.None, false)]
    public void BlocksNewCheckout_StoredSubscription_BlocksWhileItExistsOnStripe(SubscriptionStatus status, bool expected)
    {
        var org = new OrgEntity { SubscriptionId = "sub_test", SubscriptionStatus = status };

        Assert.Equal(expected, BillingSubscriptionPolicy.BlocksNewCheckout(org));
    }

    [Fact]
    public void BlocksNewCheckout_StatusWithoutSubscriptionId_DoesNotBlock()
    {
        var org = new OrgEntity { SubscriptionId = null, SubscriptionStatus = SubscriptionStatus.Active };

        Assert.False(BillingSubscriptionPolicy.BlocksNewCheckout(org));
    }

    [Theory]
    [InlineData("active", true)]
    [InlineData("trialing", true)]
    [InlineData("past_due", true)]
    [InlineData("unpaid", true)]
    [InlineData("incomplete", true)]
    [InlineData("paused", true)]
    [InlineData("some_future_status", true)]
    [InlineData("canceled", false)]
    [InlineData("incomplete_expired", false)]
    public void BlocksNewCheckout_StripeSubscriptionStatus_OnlyEndedSubscriptionsAllowCheckout(string stripeStatus, bool expected) =>
        Assert.Equal(expected, BillingSubscriptionPolicy.BlocksNewCheckout(stripeStatus));
}
