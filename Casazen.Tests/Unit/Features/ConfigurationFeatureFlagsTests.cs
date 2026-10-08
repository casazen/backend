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
        Assert.Equal(
            new[] { "OtaPartnerApi", "AiSupplierDiscovery", "RliProvider", "ESignProvider", "SupplierShowcaseBooking", "SupplierOnlinePayments" },
            FeatureFlags.All);
    }

    [Theory]
    [InlineData(FeatureFlags.SupplierShowcaseBooking)]
    [InlineData(FeatureFlags.SupplierOnlinePayments)]
    public void IsEnabled_SupplierFlagNotConfigured_IsOff(string flag)
    {
        Assert.False(Flags(new Dictionary<string, string?>()).IsEnabled(flag));
    }

    [Theory]
    [InlineData(FeatureFlags.SupplierShowcaseBooking, FeatureFlags.SupplierOnlinePayments)]
    [InlineData(FeatureFlags.SupplierOnlinePayments, FeatureFlags.SupplierShowcaseBooking)]
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
}
