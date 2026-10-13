using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// BL-01: the "accesso aperto" settings (<c>Entitlement:OpenAccess:{Enabled,Tier}</c>). Off by default; when on, the tier of an
/// org is at least the configured one (default Scale) and never lower than the one it pays for; a value that is not valid is
/// reported by <see cref="OpenAccess.GetErrors"/> (the startup check) and read as off by <see cref="OpenAccess.Read"/>.
/// </summary>
public class OpenAccessTests
{
    private static IConfiguration Config(params (string Key, string? Value)[] values) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(values.ToDictionary(v => v.Key, v => v.Value))
            .Build();

    private static (string Key, string? Value) Enabled(string? value) => ("Entitlement:OpenAccess:Enabled", value);

    private static (string Key, string? Value) Tier(string? value) => ("Entitlement:OpenAccess:Tier", value);

    // ─── Read ────────────────────────────────────────────────────────────────────

    [Fact]
    public void Read_NothingConfigured_IsOff()
    {
        var setting = OpenAccess.Read(Config());

        Assert.False(setting.Enabled);
        Assert.Equal(PlanTier.Starter, setting.Apply(PlanTier.Starter));
    }

    [Theory]
    [InlineData("false")]
    [InlineData("False")]
    [InlineData("")]
    [InlineData("   ")]
    public void Read_SwitchOffOrEmpty_IsOffEvenWithATier(string enabled)
    {
        var setting = OpenAccess.Read(Config(Enabled(enabled), Tier("Pro")));

        Assert.False(setting.Enabled);
        Assert.Equal(PlanTier.Starter, setting.Apply(PlanTier.Starter)); // off: nobody is raised
    }

    [Fact]
    public void Read_TierWithoutTheSwitch_IsOff()
    {
        var setting = OpenAccess.Read(Config(Tier("Scale")));

        Assert.False(setting.Enabled);
        Assert.Equal(PlanTier.Starter, setting.Apply(PlanTier.Starter));
    }

    [Theory]
    [InlineData("true")]
    [InlineData("True")]
    [InlineData("TRUE")]
    [InlineData(" true ")]
    public void Read_SwitchOnWithoutTier_UsesScale(string enabled)
    {
        var setting = OpenAccess.Read(Config(Enabled(enabled)));

        Assert.True(setting.Enabled);
        Assert.Equal(PlanTier.Scale, setting.Tier);
        Assert.Equal(PlanTier.Scale, OpenAccess.DefaultTier);
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    public void Read_SwitchOnWithEmptyTier_UsesScale(string tier)
    {
        var setting = OpenAccess.Read(Config(Enabled("true"), Tier(tier)));

        Assert.True(setting.Enabled);
        Assert.Equal(PlanTier.Scale, setting.Tier);
    }

    [Theory]
    [InlineData("Starter", PlanTier.Starter)]
    [InlineData("Pro", PlanTier.Pro)]
    [InlineData("Scale", PlanTier.Scale)]
    [InlineData("pro", PlanTier.Pro)]
    [InlineData("SCALE", PlanTier.Scale)]
    [InlineData(" Pro ", PlanTier.Pro)]
    public void Read_SwitchOnWithATier_UsesIt(string tier, PlanTier expected)
    {
        var setting = OpenAccess.Read(Config(Enabled("true"), Tier(tier)));

        Assert.True(setting.Enabled);
        Assert.Equal(expected, setting.Tier);
    }

    [Theory]
    [InlineData("yes")]
    [InlineData("1")]
    [InlineData("on")]
    [InlineData("tru")]
    [InlineData("enabled")]
    public void Read_SwitchNotABoolean_FailsClosedToOff(string enabled)
    {
        var setting = OpenAccess.Read(Config(Enabled(enabled), Tier("Scale")));

        Assert.False(setting.Enabled);
        Assert.Equal(PlanTier.Starter, setting.Apply(PlanTier.Starter));
    }

    [Theory]
    [InlineData("Enterprise")]
    [InlineData("Free")]
    [InlineData("1")]
    [InlineData("Starter,Pro")]
    [InlineData("99")]
    public void Read_SwitchOnWithUnknownTier_FailsClosedToOff(string tier)
    {
        var setting = OpenAccess.Read(Config(Enabled("true"), Tier(tier)));

        // A typo never opens the access (not even to the default tier).
        Assert.False(setting.Enabled);
        Assert.Equal(PlanTier.Starter, setting.Apply(PlanTier.Starter));
    }

    [Fact]
    public void Read_NullConfiguration_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => OpenAccess.Read(null!));
        Assert.Throws<ArgumentNullException>(() => OpenAccess.GetErrors(null!));
    }

    // ─── GetErrors (the startup check) ───────────────────────────────────────────

    [Fact]
    public void GetErrors_NothingConfigured_IsEmpty()
    {
        Assert.Empty(OpenAccess.GetErrors(Config()));
    }

    [Theory]
    [InlineData("true", "Scale")]
    [InlineData("false", "Scale")]
    [InlineData("true", "pro")]
    [InlineData("true", "")]
    [InlineData("", "")]
    [InlineData(null, null)]
    public void GetErrors_ValidOrEmptyValues_ArePassed(string? enabled, string? tier)
    {
        Assert.Empty(OpenAccess.GetErrors(Config(Enabled(enabled), Tier(tier))));
    }

    [Theory]
    [InlineData("yes")]
    [InlineData("1")]
    [InlineData("on")]
    [InlineData("tru")]
    public void GetErrors_SwitchNotABoolean_NamesTheVariableAndTheRunbook(string enabled)
    {
        var errors = OpenAccess.GetErrors(Config(Enabled(enabled)));

        var error = Assert.Single(errors);
        Assert.Contains("Entitlement__OpenAccess__Enabled must be true or false", error);
        Assert.Contains("docs/runbooks/open-access.md", error);
        Assert.DoesNotContain("Entitlement__OpenAccess__Tier", error);
    }

    [Theory]
    [InlineData("Enterprise")]
    [InlineData("Free")]
    [InlineData("1")]
    [InlineData("Starter,Pro")]
    public void GetErrors_UnknownTier_NamesTheVariableTheValidPlansAndTheRunbook(string tier)
    {
        var errors = OpenAccess.GetErrors(Config(Enabled("true"), Tier(tier)));

        var error = Assert.Single(errors);
        Assert.Contains("Entitlement__OpenAccess__Tier must be the name of a plan (Starter, Pro, Scale)", error);
        Assert.Contains("which means Scale", error);
        Assert.Contains("docs/runbooks/open-access.md", error);
        Assert.DoesNotContain("Entitlement__OpenAccess__Enabled", error);
    }

    [Fact]
    public void GetErrors_NeverRepeatsTheValuesOfTheSettings()
    {
        const string sentinelSwitch = "sentinel-switch-value";
        const string sentinelTier = "sentinel-tier-value";

        var errors = OpenAccess.GetErrors(Config(Enabled(sentinelSwitch), Tier(sentinelTier)));

        Assert.Equal(2, errors.Count);
        Assert.All(errors, error =>
        {
            Assert.DoesNotContain(sentinelSwitch, error);
            Assert.DoesNotContain(sentinelTier, error);
        });
    }

    [Fact]
    public void GetErrors_TierNotValidWhileTheSwitchIsOff_IsStillRefused()
    {
        // A wrong value found at the next deploy is better than one found the day the switch is turned on.
        var errors = OpenAccess.GetErrors(Config(Enabled("false"), Tier("Enterprise")));

        Assert.Single(errors);
    }

    [Fact]
    public void GetErrors_BothValuesWrong_ReportsBoth()
    {
        var errors = OpenAccess.GetErrors(Config(Enabled("yes"), Tier("Enterprise")));

        Assert.Equal(2, errors.Count);
        Assert.Contains(errors, e => e.StartsWith("Entitlement__OpenAccess__Enabled", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.StartsWith("Entitlement__OpenAccess__Tier", StringComparison.Ordinal));
    }

    [Fact]
    public void Names_AreTheDocumentedOnes()
    {
        Assert.Equal("Entitlement:OpenAccess:Enabled", OpenAccess.EnabledSetting);
        Assert.Equal("Entitlement:OpenAccess:Tier", OpenAccess.TierSetting);
        Assert.Equal("Entitlement__OpenAccess__Enabled", OpenAccess.EnabledVariable);
        Assert.Equal("Entitlement__OpenAccess__Tier", OpenAccess.TierVariable);
    }

    // ─── Apply: at least the configured tier, never lower than the paid one ────────

    [Theory]
    [InlineData(PlanTier.Starter, PlanTier.Scale, PlanTier.Scale)]
    [InlineData(PlanTier.Pro, PlanTier.Scale, PlanTier.Scale)]
    [InlineData(PlanTier.Scale, PlanTier.Scale, PlanTier.Scale)]
    [InlineData(PlanTier.Starter, PlanTier.Pro, PlanTier.Pro)]
    [InlineData(PlanTier.Pro, PlanTier.Pro, PlanTier.Pro)]
    [InlineData(PlanTier.Scale, PlanTier.Pro, PlanTier.Scale)] // a paid Scale is never lowered to Pro
    [InlineData(PlanTier.Starter, PlanTier.Starter, PlanTier.Starter)]
    [InlineData(PlanTier.Pro, PlanTier.Starter, PlanTier.Pro)] // a paid Pro is never lowered to Starter
    [InlineData(PlanTier.Scale, PlanTier.Starter, PlanTier.Scale)]
    public void Apply_SwitchOn_IsTheHigherOfThePaidAndTheOpenAccessTier(PlanTier paid, PlanTier openAccess, PlanTier expected)
    {
        var setting = new OpenAccessSetting(true, openAccess);

        Assert.Equal(expected, setting.Apply(paid));
    }

    [Theory]
    [InlineData(PlanTier.Starter, PlanTier.Scale)]
    [InlineData(PlanTier.Pro, PlanTier.Scale)]
    [InlineData(PlanTier.Scale, PlanTier.Scale)]
    [InlineData(PlanTier.Starter, PlanTier.Pro)]
    public void Apply_SwitchOff_ReturnsThePaidTierWhateverTheTier(PlanTier paid, PlanTier openAccess)
    {
        var setting = new OpenAccessSetting(false, openAccess);

        Assert.Equal(paid, setting.Apply(paid));
    }

    [Fact]
    public void Off_RaisesNobody()
    {
        Assert.False(OpenAccessSetting.Off.Enabled);
        Assert.All(Enum.GetValues<PlanTier>(), paid => Assert.Equal(paid, OpenAccessSetting.Off.Apply(paid)));
    }
}
