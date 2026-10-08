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

    // ─── AM-01: the org team flag ───────────────────────────────────────────────────────────────────────

    [Fact]
    public void OrgTeam_IsOffWhenMissing_AndOnlyOnWhenExplicitlyTrue()
    {
        Assert.False(Flags(new()).IsEnabled(FeatureFlags.OrgTeam));
        Assert.False(Flags(new() { ["Features:OrgTeam"] = "false" }).IsEnabled(FeatureFlags.OrgTeam));
        Assert.False(Flags(new() { ["Features:OrgTeam"] = "1" }).IsEnabled(FeatureFlags.OrgTeam));
        Assert.True(Flags(new() { ["Features:OrgTeam"] = "true" }).IsEnabled(FeatureFlags.OrgTeam));
    }

    [Fact]
    public void OrgTeam_IsExposedToTheFrontendAsACamelCaseKey()
    {
        Assert.Contains(FeatureFlags.OrgTeam, FeatureFlags.All);
        Assert.Equal("orgTeam", System.Text.Json.JsonNamingPolicy.CamelCase.ConvertName(FeatureFlags.OrgTeam));
    }

    [Fact]
    public void OrgTeam_AppSettingsDefault_IsOff()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Casazen.sln")))
            root = root.Parent;
        Assert.NotNull(root);

        var configuration = new ConfigurationBuilder()
            .AddJsonFile(Path.Combine(root.FullName, "Casazen.Web", "appsettings.json"))
            .Build();

        Assert.Equal("False", configuration["Features:OrgTeam"], ignoreCase: true);
        Assert.False(new ConfigurationFeatureFlags(configuration).IsEnabled(FeatureFlags.OrgTeam));
    }

    private static ConfigurationFeatureFlags Flags(Dictionary<string, string?> values) =>
        new(new ConfigurationBuilder().AddInMemoryCollection(values).Build());
}
