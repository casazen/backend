using Casazen.Infrastructure.Email;
using Casazen.Infrastructure.External;
using Casazen.Web.Extensions;
using Hangfire;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Email;

/// <summary>FD-13 (A9-06, A9-16): one typed email configuration, validated at startup outside Development/Testing.</summary>
public class EmailConfigurationTests
{
    private static readonly Dictionary<string, string?> CompleteConfiguration = new()
    {
        ["Email:Provider"] = "Resend",
        ["Email:ApiKey"] = "re_live_key",
        ["Email:FromAddress"] = "noreply@casazen.app",
        ["Email:FromName"] = "CasaZen",
        ["App:PublicSiteBaseUrl"] = "https://app.example.org",
    };

    [Fact]
    public void Startup_ProductionWithoutEmailConfiguration_FailsListingEveryMissingValue()
    {
        using var provider = BuildProvider(Environments.Production, new Dictionary<string, string?>());

        var ex = Assert.Throws<AggregateException>(() => provider.GetRequiredService<IStartupValidator>().Validate());

        var failures = ex.InnerExceptions.OfType<OptionsValidationException>().SelectMany(e => e.Failures).ToList();
        Assert.Contains(failures, f => f.Contains("Email__ApiKey", StringComparison.Ordinal));
        Assert.Contains(failures, f => f.Contains("Email__FromAddress", StringComparison.Ordinal));
        Assert.Contains(failures, f => f.Contains("App__PublicSiteBaseUrl", StringComparison.Ordinal));
    }

    [Fact]
    public void Startup_ProductionWithoutPublicSiteBaseUrl_Fails()
    {
        var configuration = new Dictionary<string, string?>(CompleteConfiguration) { ["App:PublicSiteBaseUrl"] = null };
        using var provider = BuildProvider(Environments.Production, configuration);

        var ex = Assert.Throws<OptionsValidationException>(() => provider.GetRequiredService<IStartupValidator>().Validate());

        Assert.Contains(ex.Failures, f => f.Contains("App__PublicSiteBaseUrl", StringComparison.Ordinal));
    }

    [Fact]
    public void Startup_ProductionWithResendTestSender_FailsBecauseItOnlyReachesTheAccountOwner()
    {
        var configuration = new Dictionary<string, string?>(CompleteConfiguration)
        {
            ["Email:FromAddress"] = "onboarding@resend.dev",
        };
        using var provider = BuildProvider(Environments.Production, configuration);

        var ex = Assert.Throws<OptionsValidationException>(() => provider.GetRequiredService<IStartupValidator>().Validate());

        Assert.Contains(ex.Failures, f => f.Contains("resend.dev", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("not-a-key")]
    [InlineData("SG.YOUR_KEY")]
    public void Startup_StagingWithNonResendApiKey_Fails(string apiKey)
    {
        var configuration = new Dictionary<string, string?>(CompleteConfiguration) { ["Email:ApiKey"] = apiKey };
        using var provider = BuildProvider(Environments.Staging, configuration);

        Assert.Throws<OptionsValidationException>(() => provider.GetRequiredService<IStartupValidator>().Validate());
    }

    [Fact]
    public void Startup_ProductionWithCompleteConfiguration_PassesAndKeepsTheConfiguredSender()
    {
        using var provider = BuildProvider(Environments.Production, CompleteConfiguration);

        provider.GetRequiredService<IStartupValidator>().Validate();

        var options = provider.GetRequiredService<IOptions<EmailOptions>>().Value;
        Assert.True(options.IsConfigured);
        Assert.Equal("noreply@casazen.app", options.FromAddress);
    }

    [Theory]
    [InlineData("Development")]
    [InlineData("Testing")]
    public void Startup_DevelopmentOrTestingWithoutConfiguration_StartsAndReportsEmailNotConfigured(string environment)
    {
        using var provider = BuildProvider(environment, new Dictionary<string, string?>());

        provider.GetRequiredService<IStartupValidator>().Validate();

        Assert.False(provider.GetRequiredService<IOptions<EmailOptions>>().Value.IsConfigured);
    }

    [Fact]
    public void Options_LegacyResendApiKeyVariable_IsUsedAsApiKey()
    {
        var configuration = new Dictionary<string, string?>(CompleteConfiguration)
        {
            ["Email:ApiKey"] = null,
            ["Email:ResendApiKey"] = "re_legacy_key",
        };
        using var provider = BuildProvider(Environments.Production, configuration);

        provider.GetRequiredService<IStartupValidator>().Validate();

        Assert.Equal("re_legacy_key", provider.GetRequiredService<IOptions<EmailOptions>>().Value.ApiKey);
    }

    [Fact]
    public void AddCasazenEmail_RegistersSingleResendEmailService()
    {
        using var provider = BuildProvider(Environments.Production, CompleteConfiguration);
        using var scope = provider.CreateScope();

        var services = scope.ServiceProvider.GetServices<IEmailService>().ToList();

        Assert.IsType<ResendEmailService>(Assert.Single(services));
        Assert.IsType<HangfireEmailQueue>(scope.ServiceProvider.GetRequiredService<IEmailQueue>());
    }

    private static ServiceProvider BuildProvider(string environmentName, Dictionary<string, string?> values)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        var environment = new Mock<IHostEnvironment>();
        environment.SetupGet(e => e.EnvironmentName).Returns(environmentName);

        var services = new ServiceCollection();
        services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddSingleton(Mock.Of<IBackgroundJobClient>());
        services.AddCasazenEmail(configuration, environment.Object);
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
    }
}
