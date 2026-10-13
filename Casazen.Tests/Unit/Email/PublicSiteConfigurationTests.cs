using Casazen.Infrastructure.Email;
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

/// <summary>
/// SE-02 (A8-02, decision D3): one public domain, <c>App:PublicSiteBaseUrl</c> (alias <c>Seo:PublicBaseUrl</c>), with no
/// default in the committed configuration: in Production a missing value stops the startup.
/// </summary>
public class PublicSiteConfigurationTests
{
    private const string PublicSite = "https://public-site.example.test";

    private static readonly Dictionary<string, string?> EmailConfiguration = new()
    {
        ["Email:Provider"] = "Resend",
        ["Email:ApiKey"] = "re_live_key",
        ["Email:FromAddress"] = "noreply@example.test",
    };

    [Fact]
    public void Startup_ProductionWithCommittedAppsettingsAndNoVariable_FailsNamingAppPublicSiteBaseUrl()
    {
        // The real appsettings.json: a domain committed there would silently satisfy the validation (A8-02).
        var configuration = new ConfigurationBuilder()
            .AddJsonFile(Path.Combine(FindSolutionRoot(), "Casazen.Web", "appsettings.json"), optional: false)
            .AddInMemoryCollection(EmailConfiguration)
            .Build();
        using var provider = BuildProvider(Environments.Production, configuration);

        var ex = Assert.Throws<OptionsValidationException>(() => provider.GetRequiredService<IStartupValidator>().Validate());

        Assert.Contains(ex.Failures, f => f.Contains("App__PublicSiteBaseUrl is missing", StringComparison.Ordinal));
    }

    [Fact]
    public void Startup_ProductionWithSeoAliasOnly_UsesItAsThePublicSite()
    {
        using var provider = BuildProvider(Environments.Production, With(("Seo:PublicBaseUrl", PublicSite)));

        provider.GetRequiredService<IStartupValidator>().Validate();

        var links = provider.GetRequiredService<PublicSiteLinks>();
        Assert.Equal($"{PublicSite}/p/affitti-brevi", links.PublicPage("/p/affitti-brevi"));
        Assert.Equal($"{PublicSite}/app/supplier/inbox", links.SupplierInbox());
    }

    [Fact]
    public void Startup_ProductionWithSameValueInAppAndSeo_Starts()
    {
        using var provider = BuildProvider(
            Environments.Production,
            With(("App:PublicSiteBaseUrl", PublicSite), ("Seo:PublicBaseUrl", PublicSite.ToUpperInvariant() + "/")));

        provider.GetRequiredService<IStartupValidator>().Validate();
    }

    [Fact]
    public void Startup_ProductionWithDifferentValuesInAppAndSeo_Fails()
    {
        using var provider = BuildProvider(
            Environments.Production,
            With(("App:PublicSiteBaseUrl", PublicSite), ("Seo:PublicBaseUrl", "https://other.example.test")));

        var ex = Assert.Throws<OptionsValidationException>(() => provider.GetRequiredService<IStartupValidator>().Validate());

        Assert.Contains(ex.Failures, f => f.Contains("Seo__PublicBaseUrl differs", StringComparison.Ordinal));
    }

    [Fact]
    public void PublicPage_AppValueSet_WinsOverSeoAliasOutsideProduction()
    {
        using var provider = BuildProvider(
            "Development",
            With(("App:PublicSiteBaseUrl", PublicSite), ("Seo:PublicBaseUrl", "https://other.example.test")));

        Assert.Equal($"{PublicSite}/sitemap.xml", provider.GetRequiredService<PublicSiteLinks>().PublicPage("/sitemap.xml"));
    }

    [Theory]
    [InlineData("https://public-site.example.test/", "https://public-site.example.test")]
    [InlineData("https://Public-Site.example.test:8443/base", "https://public-site.example.test:8443")]
    public void TryGetOrigin_ValidUrl_ReturnsSchemeAndAuthority(string value, string expected)
    {
        Assert.True(PublicSiteOptions.TryGetOrigin(value, out var origin));
        Assert.Equal(expected, origin);
    }

    private static IConfiguration With(params (string Key, string? Value)[] values)
    {
        var all = new Dictionary<string, string?>(EmailConfiguration);
        foreach (var (key, value) in values)
            all[key] = value;
        return new ConfigurationBuilder().AddInMemoryCollection(all).Build();
    }

    private static ServiceProvider BuildProvider(string environmentName, IConfiguration configuration)
    {
        var environment = new Mock<IHostEnvironment>();
        environment.SetupGet(e => e.EnvironmentName).Returns(environmentName);

        var services = new ServiceCollection();
        services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddSingleton(Mock.Of<IBackgroundJobClient>());
        services.AddCasazenEmail(configuration, environment.Object);
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
    }

    private static string FindSolutionRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Casazen.sln")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("Casazen.sln not found above the test output folder.");
    }
}
