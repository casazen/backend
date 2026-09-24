using System.Net;
using Casazen.Web.Configuration;
using Casazen.Web.Extensions;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Xunit;

namespace Casazen.Tests.Unit.Infrastructure;

/// <summary>
/// FD-17 (A3-29, A9-28): the API accepts only the configured origins, previews of our own Vercel project only with
/// <c>Cors:VercelPreviewPattern</c>, and never with credentials.
/// </summary>
public class CorsPolicyTests
{
    private const string WebApp = "https://app.casazen.test";
    private const string PreviewPattern = "casazen-web-git-[a-z0-9-]+-casazen-team";
    private const string TestingEnvironment = "Testing";

    [Theory]
    [InlineData("https://evil.vercel.app")]
    [InlineData("https://casazen-web-git-main-casazen-team.vercel.app")]
    [InlineData("https://app.casazen.test.evil.com")]
    [InlineData("http://app.casazen.test")]
    [InlineData("http://localhost:5173")]
    [InlineData("null")]
    public async Task Preflight_OriginNotConfigured_IsRejected(string origin)
    {
        await using var app = await StartAsync(Environments.Production, new() { ["Cors:AllowedOrigins"] = WebApp });

        using var response = await PreflightAsync(app, origin);

        Assert.False(response.Headers.Contains("Access-Control-Allow-Origin"));
    }

    [Theory]
    [InlineData(WebApp)]
    [InlineData("https://second.casazen.test")]
    public async Task Preflight_ConfiguredOrigin_IsAllowedWithoutCredentials(string origin)
    {
        await using var app = await StartAsync(
            Environments.Production,
            new() { ["Cors:AllowedOrigins"] = $"{WebApp}/, https://Second.casazen.test" });

        using var response = await PreflightAsync(app, origin);

        Assert.Equal(origin, Assert.Single(response.Headers.GetValues("Access-Control-Allow-Origin")));
        Assert.Contains("authorization", response.Headers.GetValues("Access-Control-Allow-Headers").Single(), StringComparison.OrdinalIgnoreCase);
        Assert.False(response.Headers.Contains("Access-Control-Allow-Credentials"));
    }

    [Fact]
    public async Task Get_ConfiguredOriginAsArray_IsAllowed()
    {
        await using var app = await StartAsync(
            Environments.Production,
            new() { ["Cors:AllowedOrigins:0"] = "https://other.casazen.test", ["Cors:AllowedOrigins:1"] = WebApp });

        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/ping");
        request.Headers.Add("Origin", WebApp);
        using var response = await app.GetTestClient().SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(WebApp, Assert.Single(response.Headers.GetValues("Access-Control-Allow-Origin")));
        Assert.False(response.Headers.Contains("Access-Control-Allow-Credentials"));
    }

    [Fact]
    public async Task Preflight_VercelPreviewWithoutPattern_IsRejected()
    {
        await using var app = await StartAsync(Environments.Production, new() { ["Cors:AllowedOrigins"] = WebApp });

        using var response = await PreflightAsync(app, "https://casazen-web-git-feature-x-casazen-team.vercel.app");

        Assert.False(response.Headers.Contains("Access-Control-Allow-Origin"));
    }

    [Theory]
    [InlineData("https://casazen-web-git-feature-x-casazen-team.vercel.app")]
    [InlineData("https://CASAZEN-WEB-GIT-MAIN-CASAZEN-TEAM.vercel.app")]
    public async Task Preflight_VercelPreviewMatchingPattern_IsAllowed(string origin)
    {
        await using var app = await StartAsync(Environments.Production, new()
        {
            ["Cors:AllowedOrigins"] = WebApp,
            ["Cors:VercelPreviewPattern"] = PreviewPattern,
        });

        using var response = await PreflightAsync(app, origin);

        Assert.Equal(origin, Assert.Single(response.Headers.GetValues("Access-Control-Allow-Origin")));
        Assert.False(response.Headers.Contains("Access-Control-Allow-Credentials"));
    }

    [Theory]
    [InlineData("https://evil.vercel.app")]
    [InlineData("https://casazen-web-git-main-other-team.vercel.app")]
    [InlineData("https://casazen-web-git-main-casazen-team-evil.vercel.app")]
    [InlineData("https://evil.casazen-web-git-main-casazen-team.vercel.app")]
    [InlineData("http://casazen-web-git-main-casazen-team.vercel.app")]
    [InlineData("https://casazen-web-git-main-casazen-team.vercel.app:8443")]
    [InlineData("https://casazen-web-git-main-casazen-team.vercel.app.evil.com")]
    public async Task Preflight_VercelHostNotMatchingPattern_IsRejected(string origin)
    {
        await using var app = await StartAsync(Environments.Production, new()
        {
            ["Cors:AllowedOrigins"] = WebApp,
            ["Cors:VercelPreviewPattern"] = PreviewPattern,
        });

        using var response = await PreflightAsync(app, origin);

        Assert.False(response.Headers.Contains("Access-Control-Allow-Origin"));
    }

    [Fact]
    public async Task Preflight_LoopbackOriginInDevelopment_IsAllowedWithoutConfiguration()
    {
        await using var app = await StartAsync(Environments.Development, new());

        using var response = await PreflightAsync(app, "http://localhost:5173");

        Assert.Equal("http://localhost:5173", Assert.Single(response.Headers.GetValues("Access-Control-Allow-Origin")));
    }

    [Fact]
    public async Task Preflight_OriginAcceptedByOriginSource_IsAllowed()
    {
        const string customDomain = "https://www.villa-host.test";
        await using var app = await StartAsync(
            Environments.Production,
            new() { ["Cors:AllowedOrigins"] = WebApp },
            services => services.AddScoped<ICorsOriginSource>(_ => new FixedOriginSource(customDomain)));

        using var allowed = await PreflightAsync(app, customDomain);
        using var rejected = await PreflightAsync(app, "https://www.other-host.test");

        Assert.Equal(customDomain, Assert.Single(allowed.Headers.GetValues("Access-Control-Allow-Origin")));
        Assert.False(rejected.Headers.Contains("Access-Control-Allow-Origin"));
    }

    [Fact]
    public async Task Start_ProductionWithoutAllowedOrigins_FailsWithExplicitMessage()
    {
        var ex = await Assert.ThrowsAsync<OptionsValidationException>(() =>
            StartAsync(Environments.Production, new() { ["Cors:AllowedOrigins"] = "https://your-frontend.vercel.app" }));

        Assert.Contains("Cors__AllowedOrigins is missing", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Cors:AllowedOrigins", "https://*.vercel.app", "not an origin")]
    [InlineData("Cors:AllowedOrigins", "https://app.casazen.test/app", "not an origin")]
    [InlineData("Cors:AllowedOrigins", "app.casazen.test", "not an origin")]
    [InlineData("Cors:VercelPreviewPattern", "casazen-(", "not a valid regular expression")]
    public async Task Start_MalformedValue_FailsWithExplicitMessage(string key, string value, string expected)
    {
        var config = new Dictionary<string, string?> { ["Cors:AllowedOrigins"] = WebApp, [key] = value };

        var ex = await Assert.ThrowsAsync<OptionsValidationException>(() => StartAsync(TestingEnvironment, config));

        Assert.Contains(expected, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Start_TestingWithoutAllowedOrigins_Starts()
    {
        await using var app = await StartAsync(TestingEnvironment, new());

        using var response = await PreflightAsync(app, WebApp);

        Assert.False(response.Headers.Contains("Access-Control-Allow-Origin"));
    }

    private static async Task<HttpResponseMessage> PreflightAsync(WebApplication app, string origin)
    {
        using var request = new HttpRequestMessage(HttpMethod.Options, "/api/ping");
        request.Headers.Add("Origin", origin);
        request.Headers.Add("Access-Control-Request-Method", "GET");
        request.Headers.Add("Access-Control-Request-Headers", "authorization");
        return await app.GetTestClient().SendAsync(request);
    }

    private static async Task<WebApplication> StartAsync(
        string environment,
        Dictionary<string, string?> configuration,
        Action<IServiceCollection>? configureServices = null)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = environment });
        builder.WebHost.UseTestServer();
        builder.Configuration.Sources.Clear();
        builder.Configuration.AddInMemoryCollection(configuration);
        builder.Services.AddCasazenCors();
        configureServices?.Invoke(builder.Services);

        var app = builder.Build();
        app.UseCors(CasazenCorsPolicyProvider.PolicyName);
        app.MapGet("/api/ping", () => "pong");
        try
        {
            await app.StartAsync();
        }
        catch
        {
            await app.DisposeAsync();
            throw;
        }

        return app;
    }

    private sealed class FixedOriginSource(string origin) : ICorsOriginSource
    {
        public ValueTask<bool> IsOriginAllowedAsync(string candidate, CancellationToken cancellationToken) =>
            ValueTask.FromResult(string.Equals(candidate, origin, StringComparison.OrdinalIgnoreCase));
    }
}
