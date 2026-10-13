using Casazen.Core.Features;
using Casazen.Infrastructure.Features;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Casazen.Tests.Unit.Features;

/// <summary>UI-12a: <c>Features:InAppNotifications</c> is off unless it is explicitly <c>true</c>, and is exposed to the frontend.</summary>
public class InAppNotificationsFlagTests
{
    [Fact]
    public void Flag_IsNamedAsDocumented_ListedAfterThePropertyModeFlag_AndOnlyOnce()
    {
        Assert.Equal("InAppNotifications", FeatureFlags.InAppNotifications);
        Assert.Contains(FeatureFlags.InAppNotifications, FeatureFlags.All);
        Assert.Equal(FeatureFlags.All.Count, FeatureFlags.All.Distinct().Count());
        // After the flag of PM-02, in the order the tasks landed in.
        Assert.True(
            FeatureFlags.All.ToList().IndexOf(FeatureFlags.InAppNotifications) > FeatureFlags.All.ToList().IndexOf(FeatureFlags.PropertyModeChange));
    }

    [Fact]
    public void Flag_IsExposedToTheFrontendAsACamelCaseKey()
    {
        Assert.Equal("inAppNotifications", System.Text.Json.JsonNamingPolicy.CamelCase.ConvertName(FeatureFlags.InAppNotifications));
    }

    [Theory]
    [InlineData("true", true)]
    [InlineData("True", true)]
    [InlineData("false", false)]
    [InlineData("", false)]
    [InlineData("1", false)]
    [InlineData("yes", false)]
    public void IsEnabled_IsOnOnlyWhenExplicitlyTrue(string value, bool expected)
    {
        var flags = Flags(new Dictionary<string, string?> { ["Features:InAppNotifications"] = value });

        Assert.Equal(expected, flags.IsEnabled(FeatureFlags.InAppNotifications));
    }

    [Fact]
    public void IsEnabled_NotConfigured_IsOff()
    {
        Assert.False(Flags(new Dictionary<string, string?>()).IsEnabled(FeatureFlags.InAppNotifications));
    }

    [Fact]
    public void IsEnabled_OnDoesNotTurnAnyOtherFlagOn_AndNoOtherFlagTurnsItOn()
    {
        var only = Flags(new Dictionary<string, string?> { ["Features:InAppNotifications"] = "true" });
        Assert.All(
            FeatureFlags.All.Where(flag => flag != FeatureFlags.InAppNotifications),
            flag => Assert.False(only.IsEnabled(flag), $"{flag} must stay off"));

        var allButThis = Flags(FeatureFlags.All
            .Where(flag => flag != FeatureFlags.InAppNotifications)
            .ToDictionary(flag => $"Features:{flag}", _ => (string?)"true"));
        Assert.False(allButThis.IsEnabled(FeatureFlags.InAppNotifications));
    }

    [Fact]
    public void AppSettings_ListsTheFlagAndItIsOffByDefault()
    {
        var configuration = new ConfigurationBuilder()
            .AddJsonFile(Path.Combine(FindRepositoryRoot(), "Casazen.Web", "appsettings.json"), optional: false)
            .Build();

        Assert.Equal("False", configuration[$"{FeatureFlags.SectionName}:{FeatureFlags.InAppNotifications}"]);
        Assert.False(new ConfigurationFeatureFlags(configuration).IsEnabled(FeatureFlags.InAppNotifications));
    }

    private static ConfigurationFeatureFlags Flags(Dictionary<string, string?> values) =>
        new(new ConfigurationBuilder().AddInMemoryCollection(values).Build());

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Casazen.sln")))
            directory = directory.Parent;

        return directory?.FullName ?? throw new InvalidOperationException("Casazen.sln not found above the test output folder.");
    }
}
