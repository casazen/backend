using Casazen.Core.Exceptions;
using Casazen.Core.Services;
using Casazen.Infrastructure.Services;
using Xunit;

namespace Casazen.Tests.Unit.Services;

public class CommercialCheckoutTests
{
    [Fact]
    public void Agency_ChargesBaseAndAddonsForEachRoom()
    {
        var options = StripeBillingService.BuildCommercialCheckoutOptions("cus_test", "price_agency",
            "https://example.test/onboarding?checkout=success", "https://example.test/onboarding?checkout=cancel", [],
            new CommercialSignup { Profile = "agency", AgencyName = "Demo Agency", PlanTier = "Agency", Rooms = 3, Addons = ["channels"] },
            _ => "price_channels");
        Assert.Equal(2, options.LineItems.Count);
        Assert.All(options.LineItems, item => Assert.Equal(3L, item.Quantity));
        Assert.Equal("price_agency", options.LineItems[0].Price);
        Assert.Equal("price_channels", options.LineItems[1].Price);
        Assert.Equal("subscription", options.Mode);
    }
    [Fact]
    public void MissingAddonPrice_RefusesCheckoutInsteadOfSilentlyDroppingTheCharge() =>
        Assert.Throws<DomainRuleException>(() => StripeBillingService.BuildCommercialCheckoutOptions("cus_test", "price_pro",
            "https://example.test", "https://example.test", [],
            new CommercialSignup { PlanTier = "Pro", Addons = ["channels"] }, _ => null));
}
