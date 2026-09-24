using Casazen.Core.Entities.Enums;
using Casazen.Infrastructure.Payments;
using Casazen.Infrastructure.Services;
using Casazen.Web.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Configuration;

/// <summary>
/// PL-11 (A1-31): live Stripe keys only in Production, test keys everywhere else (the Railway test environment runs as
/// Staging), and in Production every plan offered has its Stripe Price id. A wrong configuration stops the startup.
/// </summary>
public class BillingConfigurationTests
{
    private const string LiveSecretKey = "sk_live_51LiveSecretNeverShown";
    private const string TestSecretKey = "sk_test_51TestSecretNeverShown";

    private static readonly Dictionary<string, string?> AllPrices = new()
    {
        ["Billing:Prices:Starter"] = "price_1StarterLive",
        ["Billing:Prices:Pro"] = "price_1ProLive",
        ["Billing:Prices:Scale"] = "price_1ScaleLive",
    };

    [Fact]
    public void Startup_ProductionWithLiveKeyAndCommittedAppsettingsWithoutPriceIds_FailsNamingEveryPlan()
    {
        // The real appsettings.json: a price id committed there would silently satisfy the validation.
        var configuration = new ConfigurationBuilder()
            .AddJsonFile(Path.Combine(FindSolutionRoot(), "Casazen.Web", "appsettings.json"), optional: false)
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Stripe:SecretKey"] = LiveSecretKey,
                ["Stripe:PublishableKey"] = "pk_live_51Publishable",
            })
            .Build();
        using var provider = BuildProvider(Environments.Production, configuration);

        var ex = Assert.Throws<OptionsValidationException>(() => provider.GetRequiredService<IStartupValidator>().Validate());

        var failure = Assert.Single(ex.Failures);
        Assert.Contains("Billing__Prices__Starter, Billing__Prices__Pro, Billing__Prices__Scale", failure);
        Assert.DoesNotContain(LiveSecretKey, failure);
    }

    [Theory]
    [InlineData("")]
    [InlineData("price_PLACEHOLDER_pro")]
    [InlineData("price_YOUR_PRO_PRICE")]
    [InlineData("prod_1ProductNotAPrice")]
    public void Startup_ProductionWithOnePlanWithoutValidPriceId_FailsNamingThatPlan(string proPrice)
    {
        using var provider = BuildProvider(
            Environments.Production,
            With(LiveKeys(), AllPrices, new Dictionary<string, string?> { ["Billing:Prices:Pro"] = proPrice }));

        var ex = Assert.Throws<OptionsValidationException>(() => provider.GetRequiredService<IStartupValidator>().Validate());

        var failure = Assert.Single(ex.Failures);
        Assert.Contains("Billing__Prices__Pro", failure);
        Assert.DoesNotContain("Billing__Prices__Starter", failure);
    }

    [Fact]
    public void Startup_ProductionWithSamePriceIdOnTwoPlans_Fails()
    {
        using var provider = BuildProvider(
            Environments.Production,
            With(LiveKeys(), AllPrices, new Dictionary<string, string?> { ["Billing:Prices:Scale"] = "price_1ProLive" }));

        var ex = Assert.Throws<OptionsValidationException>(() => provider.GetRequiredService<IStartupValidator>().Validate());

        Assert.Contains(ex.Failures, f => f.Contains("Billing__Prices__Pro and Billing__Prices__Scale", StringComparison.Ordinal));
    }

    [Fact]
    public void Startup_ProductionWithLiveKeysAndEveryPriceId_Starts()
    {
        using var provider = BuildProvider(Environments.Production, With(LiveKeys(), AllPrices));

        provider.GetRequiredService<IStartupValidator>().Validate();
    }

    [Fact]
    public void Startup_ProductionWithoutStripeKeys_DoesNotRequirePriceIds()
    {
        // Payments stay optional (FD-12): without a secret key the plans cannot be sold anyway, /api/health/ready says so.
        using var provider = BuildProvider(
            Environments.Production,
            With(new Dictionary<string, string?> { ["Stripe:SecretKey"] = "sk_test_YOUR_KEY", ["Stripe:PublishableKey"] = "pk_test_YOUR_KEY" }));

        provider.GetRequiredService<IStartupValidator>().Validate();
    }

    [Theory]
    [InlineData("Stripe:SecretKey", TestSecretKey, "Stripe__SecretKey")]
    [InlineData("Stripe:SecretKey", "rk_test_51Restricted", "Stripe__SecretKey")]
    [InlineData("Stripe:PublishableKey", "pk_test_51Publishable", "Stripe__PublishableKey")]
    public void Startup_ProductionWithTestModeKey_Fails(string key, string value, string variable)
    {
        using var provider = BuildProvider(Environments.Production, With(LiveKeys(), AllPrices, new Dictionary<string, string?> { [key] = value }));

        var ex = Assert.Throws<OptionsValidationException>(() => provider.GetRequiredService<IStartupValidator>().Validate());

        var failure = Assert.Single(ex.Failures);
        Assert.Contains($"{variable} is a Stripe test-mode key but ASPNETCORE_ENVIRONMENT is Production", failure);
        Assert.DoesNotContain(value, failure);
    }

    [Theory]
    [InlineData("Staging", "Stripe:SecretKey", LiveSecretKey)]
    [InlineData("Staging", "Stripe:SecretKey", "rk_live_51Restricted")]
    [InlineData("Staging", "Stripe:PublishableKey", "pk_live_51Publishable")]
    [InlineData("Development", "Stripe:SecretKey", LiveSecretKey)]
    [InlineData("Testing", "Stripe:SecretKey", LiveSecretKey)]
    public void Startup_LiveModeKeyOutsideProduction_Fails(string environment, string key, string value)
    {
        using var provider = BuildProvider(environment, With(TestKeys(), new Dictionary<string, string?> { [key] = value }));

        var ex = Assert.Throws<OptionsValidationException>(() => provider.GetRequiredService<IStartupValidator>().Validate());

        var failure = Assert.Single(ex.Failures);
        Assert.Contains($"is a Stripe live-mode key but ASPNETCORE_ENVIRONMENT is {environment}", failure);
        Assert.DoesNotContain(value, failure);
    }

    [Fact]
    public void Startup_StagingWithTestKeysAndNoPriceIds_Starts()
    {
        // Outside Production a plan without price is only not purchasable (422 billing_plan_unavailable).
        using var provider = BuildProvider(Environments.Staging, With(TestKeys()));

        provider.GetRequiredService<IStartupValidator>().Validate();
    }

    [Theory]
    [InlineData("sk_test_abc", StripeKeyMode.Test)]
    [InlineData("rk_test_abc", StripeKeyMode.Test)]
    [InlineData("pk_test_abc", StripeKeyMode.Test)]
    [InlineData(" sk_live_abc ", StripeKeyMode.Live)]
    [InlineData("rk_live_abc", StripeKeyMode.Live)]
    [InlineData("pk_live_abc", StripeKeyMode.Live)]
    [InlineData("whsec_abc", StripeKeyMode.Unknown)]
    [InlineData("SK_LIVE_abc", StripeKeyMode.Unknown)]
    [InlineData(null, StripeKeyMode.Unknown)]
    public void StripeKeyModesOf_Prefix_ReturnsMode(string? key, StripeKeyMode expected)
    {
        Assert.Equal(expected, StripeKeyModes.Of(key));
    }

    [Fact]
    public void BillingPricesResolve_PlaceholderOrEmpty_IsNotAPrice()
    {
        var configuration = Configuration(new()
        {
            ["Billing:Prices:Starter"] = "price_PLACEHOLDER_starter",
            ["Billing:Prices:Pro"] = " price_1Pro ",
            ["Billing:Prices:Scale"] = "",
        });

        Assert.Null(BillingPrices.Resolve(configuration, PlanTier.Starter));
        Assert.Equal("price_1Pro", BillingPrices.Resolve(configuration, PlanTier.Pro));
        Assert.Null(BillingPrices.Resolve(configuration, PlanTier.Scale));
        Assert.Equal(PlanTier.Pro, BillingPrices.TierOf(configuration, "price_1Pro"));
        Assert.Null(BillingPrices.TierOf(configuration, "price_PLACEHOLDER_starter"));
    }

    private static Dictionary<string, string?> LiveKeys() => new()
    {
        ["Stripe:SecretKey"] = LiveSecretKey,
        ["Stripe:PublishableKey"] = "pk_live_51Publishable",
    };

    private static Dictionary<string, string?> TestKeys() => new()
    {
        ["Stripe:SecretKey"] = TestSecretKey,
        ["Stripe:PublishableKey"] = "pk_test_51Publishable",
    };

    private static IConfiguration With(params Dictionary<string, string?>[] layers)
    {
        var values = new Dictionary<string, string?>();
        foreach (var layer in layers)
        {
            foreach (var (key, value) in layer)
                values[key] = value;
        }

        return Configuration(values);
    }

    private static IConfiguration Configuration(Dictionary<string, string?> values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values).Build();

    private static ServiceProvider BuildProvider(string environment, IConfiguration configuration)
    {
        var hostEnvironment = new Mock<IHostEnvironment>();
        hostEnvironment.SetupGet(e => e.EnvironmentName).Returns(environment);

        var services = new ServiceCollection();
        services.AddCasazenBillingConfiguration(configuration, hostEnvironment.Object);
        return services.BuildServiceProvider();
    }

    private static string FindSolutionRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Casazen.sln")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("Casazen.sln not found above the test output folder.");
    }
}
