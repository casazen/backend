using Casazen.Infrastructure.Email;
using Casazen.Infrastructure.Storage;
using Casazen.Web.Configuration;
using Casazen.Web.HealthChecks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.HealthChecks;

/// <summary>FD-12 (A9-19, A3-24): configuration checks of /api/health/ready. Optional integrations are degraded.</summary>
public class ConfigurationHealthChecksTests
{
    private const string SecretValue = "sk_live_51SecretValueNeverShown";
    private const string WebhookValue = "whsec_PlatformSecretNeverShown";
    private const string ConnectWebhookValue = "whsec_ConnectSecretNeverShown";

    private static readonly Dictionary<string, string?> CompleteStripe = new()
    {
        ["Stripe:SecretKey"] = SecretValue,
        ["Stripe:PublishableKey"] = "pk_live_51PublishableValue",
        ["Stripe:WebhookSecret"] = WebhookValue,
        ["Stripe:ConnectWebhookSecret"] = ConnectWebhookValue,
    };

    [Fact]
    public async Task StripeCheck_AllKeysConfigured_ReturnsHealthy()
    {
        var result = await CheckStripeAsync(CompleteStripe);

        Assert.Equal(HealthStatus.Healthy, result.Status);
        Assert.Contains("live mode", result.Description);
    }

    [Fact]
    public async Task StripeCheck_ConnectWebhookSecretAndPublishableKeyMissing_ReturnsDegradedNamingOnlyVariables()
    {
        var configuration = new Dictionary<string, string?>(CompleteStripe)
        {
            ["Stripe:ConnectWebhookSecret"] = null,
            ["Stripe:PublishableKey"] = null,
        };

        var result = await CheckStripeAsync(configuration);

        Assert.Equal(HealthStatus.Degraded, result.Status);
        Assert.Contains("Stripe__ConnectWebhookSecret", result.Description);
        Assert.Contains("Stripe__PublishableKey", result.Description);
        Assert.DoesNotContain("Stripe__SecretKey", result.Description);
        Assert.DoesNotContain(SecretValue, result.Description);
        Assert.DoesNotContain(WebhookValue, result.Description);
    }

    [Fact]
    public async Task StripeCheck_AppsettingsPlaceholders_ReturnsDegraded()
    {
        // The values committed in appsettings.json must never count as configured.
        var result = await CheckStripeAsync(new Dictionary<string, string?>
        {
            ["Stripe:SecretKey"] = "sk_test_YOUR_KEY",
            ["Stripe:PublishableKey"] = "pk_test_YOUR_KEY",
            ["Stripe:WebhookSecret"] = "whsec_YOUR_SECRET",
            ["Stripe:ConnectWebhookSecret"] = "whsec_YOUR_CONNECT_SECRET",
        });

        Assert.Equal(HealthStatus.Degraded, result.Status);
        Assert.Contains("Stripe__SecretKey, Stripe__PublishableKey, Stripe__WebhookSecret, Stripe__ConnectWebhookSecret", result.Description);
    }

    [Fact]
    public async Task StripeCheck_LiveSecretWithTestPublishableKey_ReturnsDegraded()
    {
        var configuration = new Dictionary<string, string?>(CompleteStripe) { ["Stripe:PublishableKey"] = "pk_test_51Other" };

        var result = await CheckStripeAsync(configuration);

        Assert.Equal(HealthStatus.Degraded, result.Status);
        Assert.Contains("different modes", result.Description);
    }

    [Fact]
    public async Task StripeCheck_WebhookSecretWithWrongPrefix_ReturnsDegraded()
    {
        var configuration = new Dictionary<string, string?>(CompleteStripe) { ["Stripe:ConnectWebhookSecret"] = "sk_live_wrong_value" };

        var result = await CheckStripeAsync(configuration);

        Assert.Equal(HealthStatus.Degraded, result.Status);
        Assert.Contains("Stripe__ConnectWebhookSecret is not a webhook signing secret", result.Description);
        Assert.DoesNotContain("sk_live_wrong_value", result.Description);
    }

    [Fact]
    public async Task Auth0Check_JwtSettingsAndManagementClient_ReturnsHealthy()
    {
        var result = await CheckAuth0Async(new Auth0Options
        {
            Domain = "casazen.eu.auth0.com",
            Audience = "https://casazen-api",
            ManagementClientId = "m2m-client",
            ManagementClientSecret = "m2m-secret",
        });

        Assert.Equal(HealthStatus.Healthy, result.Status);
    }

    [Fact]
    public async Task Auth0Check_WithoutManagementClient_ReturnsDegraded()
    {
        var result = await CheckAuth0Async(new Auth0Options { Domain = "casazen.eu.auth0.com", Audience = "https://casazen-api" });

        Assert.Equal(HealthStatus.Degraded, result.Status);
        Assert.Contains("Auth0__ManagementClientId", result.Description);
    }

    [Fact]
    public async Task Auth0Check_LegacyStaticToken_ReturnsDegraded()
    {
        var result = await CheckAuth0Async(new Auth0Options
        {
            Domain = "casazen.eu.auth0.com",
            Audience = "https://casazen-api",
            ManagementApiToken = "eyJ.legacy.token",
        });

        Assert.Equal(HealthStatus.Degraded, result.Status);
        Assert.Contains("deprecated", result.Description);
        Assert.DoesNotContain("eyJ.legacy.token", result.Description);
    }

    [Fact]
    public async Task Auth0Check_PlaceholderDomainInProduction_ReturnsUnhealthy()
    {
        var result = await CheckAuth0Async(
            new Auth0Options { Domain = "your-domain.auth0.com", Audience = "https://casazen-api" },
            Environments.Production);

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
        Assert.Contains("Auth0__Domain", result.Description);
    }

    [Fact]
    public async Task Auth0Check_PlaceholderDomainInDevelopment_ReturnsDegraded()
    {
        var result = await CheckAuth0Async(
            new Auth0Options { Domain = "your-domain.auth0.com", Audience = "https://casazen-api" },
            Environments.Development);

        Assert.Equal(HealthStatus.Degraded, result.Status);
    }

    [Fact]
    public async Task EmailCheck_ProviderNotConfigured_ReturnsDegraded()
    {
        var result = await CheckEmailAsync(new EmailOptions(), "https://app.example.org");

        Assert.Equal(HealthStatus.Degraded, result.Status);
        Assert.Contains("Email__ApiKey", result.Description);
    }

    [Fact]
    public async Task EmailCheck_PublicSiteBaseUrlMissing_ReturnsDegraded()
    {
        var result = await CheckEmailAsync(ConfiguredEmail(), publicSiteBaseUrl: null);

        Assert.Equal(HealthStatus.Degraded, result.Status);
        Assert.Contains("App__PublicSiteBaseUrl", result.Description);
    }

    [Fact]
    public async Task EmailCheck_Configured_ReturnsHealthy()
    {
        var result = await CheckEmailAsync(ConfiguredEmail(), "https://app.example.org");

        Assert.Equal(HealthStatus.Healthy, result.Status);
    }

    [Fact]
    public async Task StorageCheck_S3_ReturnsHealthy()
    {
        var check = new StorageConfigurationHealthCheck(Options.Create(new StorageOptions { Provider = StorageOptions.S3Provider }));

        var result = await check.CheckHealthAsync(new HealthCheckContext());

        Assert.Equal(HealthStatus.Healthy, result.Status);
    }

    [Fact]
    public async Task StorageCheck_LocalFileSystem_ReturnsDegraded()
    {
        var check = new StorageConfigurationHealthCheck(
            Options.Create(new StorageOptions { Provider = StorageOptions.FileSystemProvider }));

        var result = await check.CheckHealthAsync(new HealthCheckContext());

        Assert.Equal(HealthStatus.Degraded, result.Status);
    }

    private static Task<HealthCheckResult> CheckStripeAsync(Dictionary<string, string?> values)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        return new StripeConfigurationHealthCheck(configuration).CheckHealthAsync(new HealthCheckContext());
    }

    private static Task<HealthCheckResult> CheckAuth0Async(Auth0Options options, string environment = "Production")
    {
        var hostEnvironment = new Mock<IHostEnvironment>();
        hostEnvironment.SetupGet(e => e.EnvironmentName).Returns(environment);
        return new Auth0ConfigurationHealthCheck(Options.Create(options), hostEnvironment.Object)
            .CheckHealthAsync(new HealthCheckContext());
    }

    private static Task<HealthCheckResult> CheckEmailAsync(EmailOptions email, string? publicSiteBaseUrl)
    {
        var links = new PublicSiteLinks(Options.Create(new PublicSiteOptions { PublicSiteBaseUrl = publicSiteBaseUrl }));
        return new EmailConfigurationHealthCheck(Options.Create(email), links).CheckHealthAsync(new HealthCheckContext());
    }

    private static EmailOptions ConfiguredEmail() => new()
    {
        Provider = EmailOptions.ResendProvider,
        ApiKey = "re_live_key",
        FromAddress = "noreply@casazen.app",
    };
}
