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

    // ─── BL-01: the UiRedesign flag (gradual rollout of the new interface) ─────────

    [Fact]
    public void UiRedesign_IsNamedAsDocumentedAndListedForTheFrontend()
    {
        Assert.Equal("UiRedesign", FeatureFlags.UiRedesign);
        Assert.Contains(FeatureFlags.UiRedesign, FeatureFlags.All);
        Assert.Equal(FeatureFlags.All.Count, FeatureFlags.All.Distinct().Count());
    }

    [Fact]
    public void IsEnabled_UiRedesignNotConfigured_IsOff()
    {
        Assert.False(Flags(new Dictionary<string, string?>()).IsEnabled(FeatureFlags.UiRedesign));
    }

    [Theory]
    [InlineData("true", true)]
    [InlineData("True", true)]
    [InlineData("false", false)]
    [InlineData("", false)]
    [InlineData("1", false)]
    [InlineData("yes", false)]
    public void IsEnabled_UiRedesign_IsOnOnlyWhenExplicitlyTrue(string value, bool expected)
    {
        var flags = Flags(new Dictionary<string, string?> { ["Features:UiRedesign"] = value });

        Assert.Equal(expected, flags.IsEnabled(FeatureFlags.UiRedesign));
    }

    [Fact]
    public void IsEnabled_UiRedesignOn_DoesNotTurnAnyOtherFlagOn()
    {
        var flags = Flags(new Dictionary<string, string?> { ["Features:UiRedesign"] = "true" });

        Assert.True(flags.IsEnabled(FeatureFlags.UiRedesign));
        Assert.All(
            FeatureFlags.All.Where(flag => flag != FeatureFlags.UiRedesign),
            flag => Assert.False(flags.IsEnabled(flag), $"{flag} must stay off"));
    }

    [Fact]
    public void IsEnabled_OtherFlagsOn_DoNotTurnUiRedesignOn()
    {
        var flags = Flags(FeatureFlags.All
            .Where(flag => flag != FeatureFlags.UiRedesign)
            .ToDictionary(flag => $"Features:{flag}", _ => (string?)"true"));

        Assert.False(flags.IsEnabled(FeatureFlags.UiRedesign));
    }

    [Fact]
    public void AppSettings_UiRedesignIsListedAndOffByDefault()
    {
        // The documented default (a missing value is off anyway): appsettings.json of the web project.
        var configuration = new ConfigurationBuilder()
            .AddJsonFile(Path.Combine(FindSolutionDirectory(), "Casazen.Web", "appsettings.json"), optional: false)
            .Build();

        Assert.Equal("False", configuration[$"{FeatureFlags.SectionName}:{FeatureFlags.UiRedesign}"]);
        Assert.False(new ConfigurationFeatureFlags(configuration).IsEnabled(FeatureFlags.UiRedesign));
    }

    private static string FindSolutionDirectory()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Casazen.sln")))
            directory = directory.Parent;

        return directory?.FullName ?? throw new InvalidOperationException("Casazen.sln not found above the test output folder.");
    }
}
