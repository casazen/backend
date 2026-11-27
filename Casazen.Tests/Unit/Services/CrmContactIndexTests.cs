using System.Security.Cryptography;
using System.Text;
using Casazen.Core.Features;
using Casazen.Core.Options;
using Casazen.Infrastructure.Features;
using Casazen.Infrastructure.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// E07/F06/US01/T01 (NF-95): the keyed index of the CRM (HMAC-SHA256 keyed by <c>Crm:ContactIndexKey</c>, the pattern of
/// <c>ServiceCustomerIndex</c>) and the startup validation of the <c>Crm</c> options.
/// </summary>
public class CrmContactIndexTests
{
    private const string Key = CrmTestIndex.Key;

    private static IHostEnvironment Environment(string name) => Mock.Of<IHostEnvironment>(host => host.EnvironmentName == name);

    /// <summary>
    /// Proof test (privacy rule, AC-NF95-3): the index is keyed, not the plain SHA-256 of the value, so a copy of the database cannot be used
    /// to recompute it from an address alone: another key gives another value, the same key always gives the same one, in 64 lowercase hex.
    /// </summary>
    [Fact]
    public void CrmContactIndex_Hash_IsKeyedNotPlainSha256_Test()
    {
        var index = CrmTestIndex.Create(Key);
        var plain = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("mario.rossi@example.com"))).ToLowerInvariant();

        var hash = index.Hash("mario.rossi@example.com");

        Assert.Matches("^[0-9a-f]{64}$", hash);
        Assert.NotEqual(plain, hash);
        Assert.Equal(hash, CrmTestIndex.Create(Key).Hash("mario.rossi@example.com"));
        Assert.NotEqual(hash, CrmTestIndex.Create(Key + "x").Hash("mario.rossi@example.com"));
        Assert.NotEqual(hash, index.Hash("maria.rossi@example.com"));
        Assert.DoesNotContain("mario", hash, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("Development")]
    [InlineData("Testing")]
    public void WithoutAKey_DevelopmentAndTesting_UseTheFixedDevelopmentKey(string environment)
    {
        var index = CrmTestIndex.Create(null, environment);
        var withoutKey = index.Hash("mario@example.com");

        Assert.True(index.IsConfigured);
        Assert.Equal(CrmTestIndex.Create(CrmContactIndex.DevelopmentKey, environment).Hash("mario@example.com"), withoutKey);
        Assert.Matches("^[0-9a-f]{64}$", withoutKey);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("  ")]
    public void WithoutAKey_ProductionNeverHashesWithAnEmptyKey(string? key)
    {
        var index = CrmTestIndex.Create(key);

        Assert.False(index.IsConfigured);
        var failure = Assert.Throws<InvalidOperationException>(() => index.Hash("mario@example.com"));
        Assert.Contains("Crm__ContactIndexKey", failure.Message);
        Assert.True(CrmTestIndex.Create(Key).IsConfigured);
    }

    // ─── Startup validation ──────────────────────────────────────────────────────

    private static CrmOptionsValidator Validator(bool flagOn, string environment)
    {
        var flags = new Mock<IFeatureFlags>();
        flags.Setup(f => f.IsEnabled(FeatureFlags.CrmMarketing)).Returns(flagOn);
        return new CrmOptionsValidator(flags.Object, Environment(environment));
    }

    [Fact]
    public void Validate_FlagOn_TheKeyIsRequiredOutsideDevelopmentAndTesting_AndNamesTheProblem()
    {
        var missing = Validator(flagOn: true, "Production").Validate(null, new CrmOptions());
        Assert.True(missing.Failed);
        Assert.Contains("Crm__ContactIndexKey is missing", string.Join(' ', missing.Failures!));

        Assert.True(Validator(flagOn: true, "Staging").Validate(null, new CrmOptions { ContactIndexKey = " " }).Failed);
        var tooShort = Validator(flagOn: true, "Production").Validate(null, new CrmOptions { ContactIndexKey = "short" });
        Assert.True(tooShort.Failed);
        Assert.Contains("too short", string.Join(' ', tooShort.Failures!));

        Assert.True(Validator(flagOn: true, "Development").Validate(null, new CrmOptions()).Succeeded);
        Assert.True(Validator(flagOn: true, "Testing").Validate(null, new CrmOptions()).Succeeded);
        Assert.True(Validator(flagOn: true, "Production").Validate(null, new CrmOptions { ContactIndexKey = Key }).Succeeded);
    }

    [Fact]
    public void Validate_FlagOff_NoKeyIsRequired_ButAKeyThatIsSetMustBeLongEnough()
    {
        Assert.True(Validator(flagOn: false, "Production").Validate(null, new CrmOptions()).Succeeded);
        Assert.True(Validator(flagOn: false, "Production").Validate(null, new CrmOptions { ContactIndexKey = Key }).Succeeded);
        Assert.True(Validator(flagOn: false, "Production").Validate(null, new CrmOptions { ContactIndexKey = "short" }).Failed);
    }

    [Fact]
    public void Validate_ARelayDomainThatIsNotABareHostName_StopsTheStartupOutsideDevelopmentAndTesting()
    {
        var options = new CrmOptions { ContactIndexKey = Key, OtaForwardDomains = ["guest.example.com", "https://relay.example.com/path"] };

        var result = Validator(flagOn: true, "Production").Validate(null, options);

        Assert.True(result.Failed);
        Assert.Single(result.Failures!);
        Assert.Contains("Crm__OtaForwardDomains__1", result.Failures!.Single());
        Assert.True(Validator(flagOn: true, "Production").Validate(null, new CrmOptions { ContactIndexKey = Key, OtaForwardDomains = ["guest.example.com"] }).Succeeded);
    }

    [Theory]
    [InlineData("guest.example.com", true)]
    [InlineData("Relay-1.Example.IT", true)]
    [InlineData("https://guest.example.com", false)]
    [InlineData("guest.example.com/path", false)]
    [InlineData("guest.example.com:443", false)]
    [InlineData("user@guest.example.com", false)]
    [InlineData("*.example.com", false)]
    [InlineData("example", false)]
    [InlineData("example.com.", false)]
    [InlineData(".example.com", false)]
    [InlineData("-bad.example.com", false)]
    [InlineData("192.0.2.1", false)]
    [InlineData(" guest.example.com", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsBareHostName_AcceptsOnlyAHostNameAndNothingAroundIt(string? value, bool expected) =>
        Assert.Equal(expected, CrmOptions.IsBareHostName(value));

    [Theory]
    [InlineData(0, 3, true)]
    [InlineData(1, 1, false)]
    [InlineData(720, 10, false)]
    [InlineData(721, 3, true)]
    [InlineData(72, 0, true)]
    [InlineData(72, 11, true)]
    public void Validate_TheLimitsOfTheDoubleOptIn_AreCheckedWhateverTheFlag(int validityHours, int maxSends, bool fails)
    {
        var options = new CrmOptions { ConfirmationLinkValidityHours = validityHours, ConfirmationMaxSends = maxSends };

        Assert.Equal(fails, Validator(flagOn: false, "Production").Validate(null, options).Failed);
    }

    [Fact]
    public void Options_TheDefaults_AreOffAndEmpty_AndBindFromTheConfigurationSectionOfTheRunbook()
    {
        var defaults = new CrmOptions();
        Assert.Null(defaults.ContactIndexKey);
        Assert.False(defaults.OctorateContactsEnabled);
        Assert.False(defaults.BookingEngineSoftOptIn);
        Assert.Empty(defaults.OtaForwardDomains);
        Assert.Equal((72, 3), (defaults.ConfirmationLinkValidityHours, defaults.ConfirmationMaxSends));

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Crm:ContactIndexKey"] = Key,
                ["Crm:OctorateContactsEnabled"] = "true",
                ["Crm:OtaForwardDomains:0"] = "guest.example.com",
                ["Crm:OtaForwardDomains:1"] = "relay.example.it",
                ["Crm:ConfirmationLinkValidityHours"] = "48",
                ["Crm:ConfirmationMaxSends"] = "2",
            })
            .Build();

        var bound = configuration.GetSection(CrmOptions.SectionName).Get<CrmOptions>()!;

        Assert.Equal(Key, bound.ContactIndexKey);
        Assert.True(bound.OctorateContactsEnabled);
        Assert.False(bound.BookingEngineSoftOptIn);
        Assert.Equal(new[] { "guest.example.com", "relay.example.it" }, bound.OtaForwardDomains);
        Assert.Equal((48, 2), (bound.ConfirmationLinkValidityHours, bound.ConfirmationMaxSends));
    }

    [Fact]
    public void TheFlag_IsListedAndOffByDefault()
    {
        Assert.Equal("CrmMarketing", FeatureFlags.CrmMarketing);
        Assert.Contains(FeatureFlags.CrmMarketing, FeatureFlags.All);
        Assert.False(new ConfigurationFeatureFlags(new ConfigurationBuilder().Build()).IsEnabled(FeatureFlags.CrmMarketing));
    }
}
