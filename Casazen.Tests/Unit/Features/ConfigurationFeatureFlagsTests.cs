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

    // ─── SP-02: the two flags of the supplier showcase and payments ──────────────

    [Fact]
    public void All_SupplierFlags_AreListedLastInTheOrderExposedToTheFrontend()
    {
        Assert.Equal("SupplierShowcaseBooking", FeatureFlags.SupplierShowcaseBooking);
        Assert.Equal("SupplierOnlinePayments", FeatureFlags.SupplierOnlinePayments);
        Assert.Equal("SupplierRequestAutoCancel", FeatureFlags.SupplierRequestAutoCancel);
        // PM-02: the flag of the scheduled change of rental mode is appended after UiRedesign.
        Assert.Equal("PropertyModeChange", FeatureFlags.PropertyModeChange);
        Assert.Equal(
            new[]
            {
                "OtaPartnerApi",
                "AiSupplierDiscovery",
                "RliProvider",
                "ESignProvider",
                "SupplierShowcaseBooking",
                "SupplierOnlinePayments",
                "SupplierRequestAutoCancel",
                "UiRedesign",
                "PropertyModeChange",
            },
            FeatureFlags.All);
    }

    [Theory]
    [InlineData(FeatureFlags.SupplierShowcaseBooking)]
    [InlineData(FeatureFlags.SupplierOnlinePayments)]
    [InlineData(FeatureFlags.SupplierRequestAutoCancel)]
    public void IsEnabled_SupplierFlagNotConfigured_IsOff(string flag)
    {
        Assert.False(Flags(new Dictionary<string, string?>()).IsEnabled(flag));
    }

    [Theory]
    [InlineData(FeatureFlags.SupplierShowcaseBooking, FeatureFlags.SupplierOnlinePayments)]
    [InlineData(FeatureFlags.SupplierOnlinePayments, FeatureFlags.SupplierShowcaseBooking)]
    [InlineData(FeatureFlags.SupplierRequestAutoCancel, FeatureFlags.SupplierOnlinePayments)]
    [InlineData(FeatureFlags.SupplierOnlinePayments, FeatureFlags.SupplierRequestAutoCancel)]
    public void IsEnabled_OneSupplierFlagOn_DoesNotTurnTheOtherOn(string on, string other)
    {
        var flags = Flags(new Dictionary<string, string?> { [$"Features:{on}"] = "true" });

        Assert.True(flags.IsEnabled(on));
        Assert.False(flags.IsEnabled(other));
    }

    [Fact]
    public void AppSettings_EveryFlagIsListedAndOffByDefault()
    {
        // The documented default of each flag (a missing value is off anyway): appsettings.json of the web project.
        var configuration = new ConfigurationBuilder()
            .AddJsonFile(Path.Combine(FindRepositoryRoot(), "Casazen.Web", "appsettings.json"), optional: false)
            .Build();
        var flags = new ConfigurationFeatureFlags(configuration);

        Assert.All(FeatureFlags.All, flag =>
        {
            Assert.Equal("False", configuration[$"{FeatureFlags.SectionName}:{flag}"]);
            Assert.False(flags.IsEnabled(flag), $"{flag} must be off by default");
        });
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Casazen.sln")))
            directory = directory.Parent;

        return directory?.FullName ?? throw new InvalidOperationException("Casazen.sln not found above the test output folder.");
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
            .AddJsonFile(Path.Combine(FindRepositoryRoot(), "Casazen.Web", "appsettings.json"), optional: false)
            .Build();

        Assert.Equal("False", configuration[$"{FeatureFlags.SectionName}:{FeatureFlags.UiRedesign}"]);
        Assert.False(new ConfigurationFeatureFlags(configuration).IsEnabled(FeatureFlags.UiRedesign));
    }
}
