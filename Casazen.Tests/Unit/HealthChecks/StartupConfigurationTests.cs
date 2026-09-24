using Casazen.Infrastructure.Data;
using Casazen.Web.Configuration;
using Casazen.Web.Extensions;
using Casazen.Web.HealthChecks;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.HealthChecks;

/// <summary>
/// FD-12 (A9-19): settings the API cannot run without stop the startup outside Development/Testing; the commit of
/// the build is read from the host variables.
/// </summary>
public class StartupConfigurationTests
{
    [Fact]
    public void Startup_ProductionWithAppsettingsAuth0Placeholders_FailsNamingDomain()
    {
        using var provider = BuildAuthenticationProvider(Environments.Production, new Dictionary<string, string?>
        {
            ["Auth0:Domain"] = "your-domain.auth0.com",
            ["Auth0:Audience"] = "https://casazen-api",
        });

        var ex = Assert.Throws<OptionsValidationException>(() => provider.GetRequiredService<IStartupValidator>().Validate());

        Assert.Contains(ex.Failures, f => f.Contains("Auth0__Domain", StringComparison.Ordinal));
        Assert.DoesNotContain(ex.Failures, f => f.Contains("Auth0__Audience", StringComparison.Ordinal));
    }

    [Fact]
    public void Startup_ProductionWithDomainIncludingScheme_Fails()
    {
        using var provider = BuildAuthenticationProvider(Environments.Production, new Dictionary<string, string?>
        {
            ["Auth0:Domain"] = "https://casazen.eu.auth0.com/",
            ["Auth0:Audience"] = "https://casazen-api",
        });

        var ex = Assert.Throws<OptionsValidationException>(() => provider.GetRequiredService<IStartupValidator>().Validate());

        Assert.Contains(ex.Failures, f => f.Contains("host only", StringComparison.Ordinal));
    }

    [Fact]
    public void Startup_ProductionWithAuth0Configured_Succeeds()
    {
        using var provider = BuildAuthenticationProvider(Environments.Production, new Dictionary<string, string?>
        {
            ["Auth0:Domain"] = "casazen.eu.auth0.com",
            ["Auth0:Audience"] = "https://casazen-api",
        });

        provider.GetRequiredService<IStartupValidator>().Validate();
    }

    [Fact]
    public void Startup_DevelopmentWithoutAuth0_IsNotValidated()
    {
        using var provider = BuildAuthenticationProvider(Environments.Development, new Dictionary<string, string?>());

        provider.GetService<IStartupValidator>()?.Validate();
        Assert.Empty(provider.GetServices<IValidateOptions<Auth0Options>>());
    }

    [Fact]
    public void EnsureDatabaseConnection_ProductionWithoutConnectionString_Throws()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            RequiredConfiguration.EnsureDatabaseConnection(string.Empty, Environment(Environments.Production)));

        Assert.Contains(RequiredConfiguration.ConnectionStringVariable, ex.Message);
    }

    [Theory]
    [InlineData("Development")]
    [InlineData("Testing")]
    public void EnsureDatabaseConnection_LocalEnvironmentWithoutConnectionString_DoesNotThrow(string environment)
    {
        RequiredConfiguration.EnsureDatabaseConnection(null, Environment(environment));
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("  ", true)]
    [InlineData("sk_test_YOUR_KEY", true)]
    [InlineData("your-domain.auth0.com", true)]
    [InlineData("dev-xxxxxxxx.us.auth0.com", true)]
    [InlineData("[whsec_...]", true)]
    [InlineData("sk_live_...", true)]
    [InlineData("whsec_abc123", false)]
    [InlineData("casazen.eu.auth0.com", false)]
    public void IsMissing_EmptyOrPlaceholder_IsMissing(string? value, bool expected)
    {
        Assert.Equal(expected, RequiredConfiguration.IsMissing(value));
    }

    [Fact]
    public void FromConfiguration_RailwayCommitSha_IsExposedLowercase()
    {
        var build = BuildInfo.FromConfiguration(Configuration(new Dictionary<string, string?>
        {
            [BuildInfo.RailwayCommitVariable] = "ABCDEF0123456789ABCDEF0123456789ABCDEF01",
            [BuildInfo.CommitVariable] = "1111111",
        }));

        Assert.Equal("abcdef0123456789abcdef0123456789abcdef01", build.CommitSha);
    }

    [Fact]
    public void FromConfiguration_NotAShaValue_IsIgnored()
    {
        var build = BuildInfo.FromConfiguration(Configuration(new Dictionary<string, string?>
        {
            [BuildInfo.RailwayCommitVariable] = "<script>",
        }));

        Assert.Null(build.CommitSha);
    }

    [Fact]
    public async Task DatabaseCheck_InMemoryInProduction_ReturnsUnhealthy()
    {
        await using var db = InMemoryDb();

        var result = await new DatabaseHealthCheck(db, Environment(Environments.Production)).CheckHealthAsync(new HealthCheckContext());

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
    }

    [Fact]
    public async Task DatabaseCheck_InMemoryInTesting_ReturnsDegraded()
    {
        await using var db = InMemoryDb();

        var result = await new DatabaseHealthCheck(db, Environment("Testing")).CheckHealthAsync(new HealthCheckContext());

        Assert.Equal(HealthStatus.Degraded, result.Status);
    }

    private static ServiceProvider BuildAuthenticationProvider(string environment, Dictionary<string, string?> values)
    {
        var hostEnvironment = new Mock<IWebHostEnvironment>();
        hostEnvironment.SetupGet(e => e.EnvironmentName).Returns(environment);

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddCasazenAuthentication(Configuration(values), hostEnvironment.Object);
        return services.BuildServiceProvider();
    }

    private static IConfiguration Configuration(Dictionary<string, string?> values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values).Build();

    private static IHostEnvironment Environment(string name)
    {
        var environment = new Mock<IHostEnvironment>();
        environment.SetupGet(e => e.EnvironmentName).Returns(name);
        return environment.Object;
    }

    private static AppDbContext InMemoryDb() =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase($"health-{Guid.NewGuid():N}").Options);
}
