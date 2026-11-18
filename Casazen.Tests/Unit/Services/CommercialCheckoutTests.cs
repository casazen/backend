using Casazen.Core.Exceptions;
using Casazen.Core.Services;
using Casazen.Infrastructure.Services;
using Xunit;
using Stripe;

namespace Casazen.Tests.Unit.Services;

public class CommercialCheckoutTests
{
    [Fact]
    public void AgencyPrice_MustMatchApprovedVolumeBandsMinimumAndQuoteThreshold()
    {
        var price = new Price
        {
            BillingScheme = "tiered",
            TiersMode = "volume",
            Tiers =
            [
                new() { UpTo = 9, FlatAmountDecimal = 10000 },
                new() { UpTo = 49, UnitAmountDecimal = 1100 },
                new() { UpTo = 149, UnitAmountDecimal = 990 },
                new() { UpTo = 399, UnitAmountDecimal = 880 },
                new() { UnitAmountDecimal = 0 },
            ],
        };
        Assert.True(AgencyUsagePricing.MatchesTariff(price, AgencyUsagePricing.Base, null));
        price.Tiers[1].UnitAmountDecimal = 1000;
        Assert.False(AgencyUsagePricing.MatchesTariff(price, AgencyUsagePricing.Base, null));
        price.Tiers[1].UnitAmountDecimal = 1100;
        price.Tiers[^1].UnitAmountDecimal = 880;
        Assert.False(AgencyUsagePricing.MatchesTariff(price, AgencyUsagePricing.Base, null));
    }

    [Fact]
    public void Agency_CollectsPaymentMethodWithOnlyMeteredLinesAndNoDeclaredQuantity()
    {
        var options = StripeBillingService.BuildCommercialCheckoutOptions("cus_test", "price_agency",
            "https://example.test/onboarding?checkout=success", "https://example.test/onboarding?checkout=cancel", [],
            new CommercialSignup { Profile = "agency", AgencyName = "Demo Agency", PlanTier = "Agency", Rooms = 37, Addons = ["channels", "revenue", "ai-agents"] },
            code => "price_" + code);
        Assert.Equal(3, options.LineItems.Count);
        Assert.All(options.LineItems, item => Assert.Null(item.Quantity));
        Assert.Equal("always", options.PaymentMethodCollection);
        Assert.Equal("flexible", options.SubscriptionData.BillingMode.Type);
        Assert.Equal(1, options.SubscriptionData.BillingCycleAnchor!.Value.Day);
        Assert.Equal(AgencyUsagePricing.Version, options.SubscriptionData.Metadata[AgencyUsagePricing.MetadataKey]);
        Assert.Equal("price_agency", options.LineItems[0].Price);
        Assert.Equal("price_revenue", options.LineItems[1].Price);
        Assert.Equal("price_ai-agents", options.LineItems[2].Price);
        Assert.Equal("subscription", options.Mode);
    }
    [Theory]
    [InlineData("Pro")]
    [InlineData("Scale")]
    public void OwnerIncludedModules_ChargeOnlyBasePrice(string tier)
    {
        var options = StripeBillingService.BuildCommercialCheckoutOptions("cus_test", "price_base",
            "https://example.test", "https://example.test", [],
            new CommercialSignup { PlanTier = tier, Addons = ["channels", "direct-booking", "compliance", "operations"] },
            _ => throw new InvalidOperationException("Included modules do not need a Stripe price"));
        Assert.Equal("price_base", Assert.Single(options.LineItems).Price);
    }

    [Fact]
    public void MissingAddonPrice_RefusesCheckoutInsteadOfSilentlyDroppingTheCharge() =>
        Assert.Throws<DomainRuleException>(() => StripeBillingService.BuildCommercialCheckoutOptions("cus_test", "price_pro",
            "https://example.test", "https://example.test", [],
            new CommercialSignup { PlanTier = "Pro", Addons = ["fiscal"] }, _ => null));
}
