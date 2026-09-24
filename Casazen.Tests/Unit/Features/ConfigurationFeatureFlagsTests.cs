using Casazen.Core.Features;
using Casazen.Infrastructure.Features;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Casazen.Tests.Unit.Features;

/// <summary>FD-20: a feature flag is on only when <c>Features:{flag}</c> is explicitly <c>true</c>.</summary>
public class ConfigurationFeatureFlagsTests
{
    [Fact]
    public void IsEnabled_FlagMissing_ReturnsFalse()
    {
        var flags = Flags(new Dictionary<string, string?>());

        Assert.False(flags.IsEnabled(FeatureFlags.OtaPartnerApi));
    }

    [Theory]
    [InlineData("true")]
    [InlineData("True")]
    public void IsEnabled_FlagTrue_ReturnsTrue(string value)
    {
        var flags = Flags(new Dictionary<string, string?> { ["Features:OtaPartnerApi"] = value });

        Assert.True(flags.IsEnabled(FeatureFlags.OtaPartnerApi));
    }

    [Theory]
    [InlineData("false")]
    [InlineData("")]
    [InlineData("1")]
    [InlineData("yes")]
    public void IsEnabled_FlagFalseOrNotBoolean_ReturnsFalse(string value)
    {
        var flags = Flags(new Dictionary<string, string?> { ["Features:OtaPartnerApi"] = value });

        Assert.False(flags.IsEnabled(FeatureFlags.OtaPartnerApi));
    }

    [Fact]
    public void IsEnabled_OtherFlagOn_DoesNotEnableThisFlag()
    {
        var flags = Flags(new Dictionary<string, string?> { ["Features:SomethingElse"] = "true" });

        Assert.False(flags.IsEnabled(FeatureFlags.OtaPartnerApi));
    }

    [Fact]
    public void All_ContainsOtaPartnerApi()
    {
        Assert.Contains(FeatureFlags.OtaPartnerApi, FeatureFlags.All);
    }

    private static ConfigurationFeatureFlags Flags(Dictionary<string, string?> values) =>
        new(new ConfigurationBuilder().AddInMemoryCollection(values).Build());
}
