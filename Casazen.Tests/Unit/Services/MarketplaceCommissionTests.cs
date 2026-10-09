using Casazen.Core.Services;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Casazen.Tests.Unit.Services;

public class MarketplaceCommissionTests
{
    [Fact]
    public void GetPercent_NotConfigured_IsThreePointFive()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection().Build();

        Assert.Equal(3.5m, MarketplaceCommission.GetPercent(configuration));
    }

    [Fact]
    public void ApplicationFeeCents_TenEuros_WithholdsThirtyFiveCents()
    {
        Assert.Equal(35, MarketplaceCommission.ApplicationFeeCents(1_000, 3.5m));
    }

    [Fact]
    public void ApplicationFeeCents_ZeroAmountOrPercent_IsZero()
    {
        Assert.Equal(0, MarketplaceCommission.ApplicationFeeCents(0, 3.5m));
        Assert.Equal(0, MarketplaceCommission.ApplicationFeeCents(1_000, 0m));
    }
}
