using Casazen.Core.Exceptions;
using Casazen.Core.Services;
using Casazen.Infrastructure.Services;
using Xunit;

namespace Casazen.Tests.Unit.Services;

public class CommercialCheckoutTests
{
    [Fact]
    public void Agency_CollectsPaymentMethodWithOnlyMeteredLinesAndNoDeclaredQuantity()
    {
        var options = StripeBillingService.BuildCommercialCheckoutOptions("cus_test", "price_agency",
            "https://example.test/onboarding?checkout=success", "https://example.test/onboarding?checkout=cancel", [],
            new CommercialSignup { Profile = "agency", AgencyName = "Demo Agency", PlanTier = "Agency", Rooms = 37, Addons = ["channels", "revenue"] },
            code => "price_" + code);
        Assert.Equal(2, options.LineItems.Count);
        Assert.All(options.LineItems, item => Assert.Null(item.Quantity));
        Assert.Equal("always", options.PaymentMethodCollection);
        Assert.Equal("flexible", options.SubscriptionData.BillingMode.Type);
        Assert.Equal(1, options.SubscriptionData.BillingCycleAnchor!.Value.Day);
        Assert.Equal(AgencyUsagePricing.Version, options.SubscriptionData.Metadata[AgencyUsagePricing.MetadataKey]);
        Assert.Equal("price_agency", options.LineItems[0].Price);
        Assert.Equal("price_revenue", options.LineItems[1].Price);
        Assert.Equal("subscription", options.Mode);
    }
    [Fact]
    public void MissingAddonPrice_RefusesCheckoutInsteadOfSilentlyDroppingTheCharge() =>
        Assert.Throws<DomainRuleException>(() => StripeBillingService.BuildCommercialCheckoutOptions("cus_test", "price_pro",
            "https://example.test", "https://example.test", [],
            new CommercialSignup { PlanTier = "Pro", Addons = ["channels"] }, _ => null));
}
