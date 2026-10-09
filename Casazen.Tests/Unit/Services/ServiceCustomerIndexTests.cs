using Casazen.Core.Features;
using Casazen.Core.Options;
using Casazen.Infrastructure.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// SP-10: the e-mail index of the customers of the suppliers (HMAC-SHA256 keyed by <c>Suppliers:CustomerIndexKey</c>) and the
/// startup validation of the booking configuration: the key is required where the booking can run outside Development and
/// Testing, the version of the privacy notice wherever the flag is on.
/// </summary>
public class ServiceCustomerIndexTests
{
    private const string Key = "0123456789abcdef0123456789abcdef-test-key";

    private static ServiceCustomerIndex Index(string? key, string environment = "Production") =>
        new(
            Options.Create(new ServiceCustomerIndexOptions { CustomerIndexKey = key }),
            Environment(environment),
            NullLogger<ServiceCustomerIndex>.Instance);

    private static IHostEnvironment Environment(string name) =>
        Mock.Of<IHostEnvironment>(environment => environment.EnvironmentName == name);

    [Fact]
    public void HashEmail_IsAHmacInLowercaseHex_TheSameForTheSameAddress()
    {
        var index = Index(Key);

        var hash = index.HashEmail("mario.rossi@example.com");

        Assert.Matches("^[0-9a-f]{64}$", hash);
        Assert.Equal(hash, index.HashEmail("mario.rossi@example.com"));
        Assert.DoesNotContain("mario", hash, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("Mario.Rossi@Example.COM")]
    [InlineData("  mario.rossi@example.com  ")]
    public void HashEmail_TwoSpellingsOfOneAddress_AreOneCustomer(string spelling) =>
        Assert.Equal(Index(Key).HashEmail("mario.rossi@example.com"), Index(Key).HashEmail(spelling));

    [Fact]
    public void HashEmail_AnotherAddress_OrAnotherKey_IsAnotherIndex()
    {
        var hash = Index(Key).HashEmail("mario.rossi@example.com");

        Assert.NotEqual(hash, Index(Key).HashEmail("maria.rossi@example.com"));
        // The key is the secret: with another one nobody can test an address against a copy of the database.
        Assert.NotEqual(hash, Index(Key + "x").HashEmail("mario.rossi@example.com"));
    }

    [Fact]
    public void HashEmail_IsTheHmacOfTheNormalizedAddress_NotAPlainSha256()
    {
        var plain = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData("mario.rossi@example.com"u8)).ToLowerInvariant();

        Assert.NotEqual(plain, Index(Key).HashEmail("mario.rossi@example.com"));
    }

    [Theory]
    [InlineData("Development")]
    [InlineData("Testing")]
    public void WithoutAKey_DevelopmentAndTesting_UseTheFixedDevelopmentKey(string environment)
    {
        var withoutKey = Index(null, environment).HashEmail("mario@example.com");

        Assert.Equal(Index(ServiceCustomerIndex.DevelopmentKey).HashEmail("mario@example.com"), withoutKey);
        Assert.Matches("^[0-9a-f]{64}$", withoutKey);
    }

    [Fact]
    public void WithoutAKey_ProductionNeverHashesWithAnEmptyKey()
    {
        var index = Index("  ");

        Assert.Throws<InvalidOperationException>(() => index.HashEmail("mario@example.com"));
    }

    // ─── Startup validation ──────────────────────────────────────────────────────

    private static ShowcaseBookingOptionsValidator Validator(bool flagOn, string environment)
    {
        var flags = new Mock<IFeatureFlags>();
        flags.Setup(f => f.IsEnabled(FeatureFlags.SupplierShowcaseBooking)).Returns(flagOn);
        return new ShowcaseBookingOptionsValidator(flags.Object, Environment(environment));
    }

    [Fact]
    public void Validate_FlagOff_NothingIsRequired()
    {
        var validator = Validator(flagOn: false, "Production");

        Assert.True(validator.Validate(null, new ShowcaseBookingOptions()).Succeeded);
        Assert.True(validator.Validate(null, new ServiceCustomerIndexOptions()).Succeeded);
    }

    [Fact]
    public void Validate_FlagOn_TheVersionOfThePrivacyNoticeIsRequired_InEveryEnvironment()
    {
        foreach (var environment in new[] { "Production", "Development", "Testing" })
        {
            var result = Validator(flagOn: true, environment).Validate(null, new ShowcaseBookingOptions());

            Assert.True(result.Failed, environment);
            Assert.Contains("Suppliers__Showcase__PrivacyNoticeVersion", string.Join(' ', result.Failures!));
        }

        Assert.True(Validator(flagOn: true, "Production").Validate(null, new ShowcaseBookingOptions { PrivacyNoticeVersion = "2026-11-v1" }).Succeeded);
    }

    [Fact]
    public void Validate_FlagOn_TheIndexKeyIsRequiredOutsideDevelopmentAndTesting()
    {
        var production = Validator(flagOn: true, "Production").Validate(null, new ServiceCustomerIndexOptions());
        Assert.True(production.Failed);
        Assert.Contains("Suppliers__CustomerIndexKey", string.Join(' ', production.Failures!));

        Assert.True(Validator(flagOn: true, "Staging").Validate(null, new ServiceCustomerIndexOptions { CustomerIndexKey = " " }).Failed);
        Assert.True(Validator(flagOn: true, "Development").Validate(null, new ServiceCustomerIndexOptions()).Succeeded);
        Assert.True(Validator(flagOn: true, "Testing").Validate(null, new ServiceCustomerIndexOptions()).Succeeded);
        Assert.True(Validator(flagOn: true, "Production").Validate(null, new ServiceCustomerIndexOptions { CustomerIndexKey = Key }).Succeeded);
    }

    [Fact]
    public void Validate_AKeyThatIsTooShort_IsRefusedWhateverTheFlag()
    {
        var result = Validator(flagOn: false, "Production").Validate(null, new ServiceCustomerIndexOptions { CustomerIndexKey = "short" });

        Assert.True(result.Failed);
        Assert.Contains("too short", string.Join(' ', result.Failures!));
    }

    [Theory]
    [InlineData(4, true)]
    [InlineData(5, false)]
    [InlineData(1440, false)]
    [InlineData(1441, true)]
    public void Validate_TheMinutesToCheckTheEmail_AreBetween5AndADay(int minutes, bool fails)
    {
        var result = Validator(flagOn: false, "Production").Validate(null, new ShowcaseBookingOptions { EmailVerificationMinutes = minutes });

        Assert.Equal(fails, result.Failed);
    }

    [Theory]
    [InlineData(29, true)]
    [InlineData(30, false)]
    [InlineData(10080, false)]
    [InlineData(10081, true)]
    public void Validate_TheMinutesToAnswerAProposal_AreBetweenHalfAnHourAndAWeek(int minutes, bool fails)
    {
        var result = Validator(flagOn: false, "Production").Validate(null, new ShowcaseBookingOptions { ProposalResponseMinutes = minutes });

        Assert.Equal(fails, result.Failed);
    }

    [Fact]
    public void Options_TheDefaults_AreThirtyMinutesAndADay_AndNoVersion()
    {
        var options = new ShowcaseBookingOptions();

        Assert.Equal(30, options.EmailVerificationMinutes);
        Assert.Equal(24 * 60, options.ProposalResponseMinutes);
        Assert.Null(options.PrivacyNoticeVersion);
        Assert.Null(options.CurrentPrivacyNoticeVersion);
        Assert.Equal("v1", new ShowcaseBookingOptions { PrivacyNoticeVersion = "  v1 " }.CurrentPrivacyNoticeVersion);
    }

    [Fact]
    public void Options_BindFromTheConfigurationSectionsOfTheRunbook()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Suppliers:Showcase:EmailVerificationMinutes"] = "45",
                ["Suppliers:Showcase:PrivacyNoticeVersion"] = "2026-11-v1",
                ["Suppliers:Showcase:ProposalResponseMinutes"] = "600",
                ["Suppliers:CustomerIndexKey"] = Key,
            })
            .Build();

        var showcase = configuration.GetSection(ShowcaseBookingOptions.SectionName).Get<ShowcaseBookingOptions>()!;
        var index = configuration.GetSection(ServiceCustomerIndexOptions.SectionName).Get<ServiceCustomerIndexOptions>()!;

        Assert.Equal((45, "2026-11-v1", 600), (showcase.EmailVerificationMinutes, showcase.PrivacyNoticeVersion, showcase.ProposalResponseMinutes));
        Assert.Equal(Key, index.CustomerIndexKey);
    }
}
