using System.Net;
using System.Text.Json;
using Casazen.Infrastructure.Data;
using Casazen.Web.Configuration;
using Casazen.Web.Extensions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Casazen.Tests.Integration;

/// <summary>
/// FD-12 (A9-19, A3-24): /api/health/live, /api/health/ready and /api/health are real, anonymous and expose only check
/// names and statuses (plus the build commit) to anonymous callers. The test host runs without Hangfire and without
/// Stripe/email configuration, so ready is <c>degraded</c> (200); an unreachable database makes it 503.
/// </summary>
public class HealthEndpointsIntegrationTests : IClassFixture<HealthEndpointsIntegrationTests.CommitShaFactory>
{
    private const string CommitSha = "0123456789abcdef0123456789abcdef01234567";

    private static readonly string[] SensitiveFragments =
    [
        "whsec_", "pk_test_integration", "test.auth0.com", "Stripe__", "Email__", "Auth0__", "App__",
        "description", "exception", "Host=", "127.0.0.1",
    ];

    private readonly CommitShaFactory _factory;

    public HealthEndpointsIntegrationTests(CommitShaFactory factory) => _factory = factory;

    [Fact]
    public async Task Live_Anonymous_Returns200WithCommit()
    {
        using var client = _factory.CreateClient();

        var response = await client.GetAsync(HealthCheckExtensions.LivePath);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = await ReadJsonAsync(response);
        Assert.Equal("healthy", body.RootElement.GetProperty("status").GetString());
        Assert.Equal(CommitSha, body.RootElement.GetProperty("commit").GetString());
        Assert.Equal(0, body.RootElement.GetProperty("checks").GetArrayLength());
    }

    [Fact]
    public async Task Ready_WithoutOptionalConfiguration_Returns200Degraded()
    {
        using var client = _factory.CreateClient();

        var response = await client.GetAsync(HealthCheckExtensions.ReadyPath);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = await ReadJsonAsync(response);
        Assert.Equal("degraded", body.RootElement.GetProperty("status").GetString());
        Assert.Equal(CommitSha, body.RootElement.GetProperty("commit").GetString());

        var checks = ReadChecks(body);
        Assert.Equal(new[] { "auth0", "database", "email", "hangfire", "storage", "stripe" }, checks.Keys.Order().ToArray());
        // Factory: Stripe secret key and Connect webhook secret are appsettings placeholders, no email provider,
        // no Hangfire (no connection string at startup), no Auth0 M2M client, local-disk storage.
        Assert.Equal("degraded", checks["stripe"]);
        Assert.Equal("degraded", checks["email"]);
        Assert.Equal("degraded", checks["hangfire"]);
        Assert.Equal("degraded", checks["auth0"]);
        Assert.Equal("degraded", checks["storage"]);
        Assert.Equal(_factory.UsesPostgreSql ? "healthy" : "degraded", checks["database"]);
    }

    [Fact]
    public async Task Health_LegacyPath_ReflectsReady()
    {
        using var client = _factory.CreateClient();

        var legacy = await client.GetAsync(HealthCheckExtensions.LegacyPath);
        var ready = await client.GetAsync(HealthCheckExtensions.ReadyPath);

        Assert.Equal(ready.StatusCode, legacy.StatusCode);
        using var legacyBody = await ReadJsonAsync(legacy);
        using var readyBody = await ReadJsonAsync(ready);
        Assert.Equal(readyBody.RootElement.GetProperty("status").GetString(), legacyBody.RootElement.GetProperty("status").GetString());
        Assert.Equal(ReadChecks(readyBody), ReadChecks(legacyBody));
    }

    [Theory]
    [InlineData(HealthCheckExtensions.ReadyPath)]
    [InlineData(HealthCheckExtensions.LegacyPath)]
    [InlineData(HealthCheckExtensions.LivePath)]
    public async Task Get_Anonymous_ExposesOnlyNamesAndStatuses(string path)
    {
        using var client = _factory.CreateClient();

        var response = await client.GetAsync(path);
        var raw = await response.Content.ReadAsStringAsync();

        foreach (var fragment in SensitiveFragments)
            Assert.DoesNotContain(fragment, raw, StringComparison.OrdinalIgnoreCase);

        using var body = JsonDocument.Parse(raw);
        Assert.Equal(new[] { "checks", "commit", "status" }, body.RootElement.EnumerateObject().Select(p => p.Name).Order().ToArray());
        foreach (var check in body.RootElement.GetProperty("checks").EnumerateArray())
            Assert.Equal(new[] { "name", "status" }, check.EnumerateObject().Select(p => p.Name).Order().ToArray());
    }

    [Fact]
    public async Task Ready_NonAdminUser_GetsNoDescriptions()
    {
        using var client = _factory.CreateAuthenticatedClient($"auth0|health-owner-{Guid.NewGuid():N}", "PropertyOwner");

        var raw = await (await client.GetAsync(HealthCheckExtensions.ReadyPath)).Content.ReadAsStringAsync();

        Assert.DoesNotContain("description", raw, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Ready_Admin_GetsDescriptionsNamingVariablesWithoutValues()
    {
        using var client = _factory.CreateAuthenticatedClient($"auth0|health-admin-{Guid.NewGuid():N}", "Admin");

        var response = await client.GetAsync(HealthCheckExtensions.ReadyPath);
        var raw = await response.Content.ReadAsStringAsync();

        using var body = JsonDocument.Parse(raw);
        var stripe = body.RootElement.GetProperty("checks").EnumerateArray()
            .Single(c => c.GetProperty("name").GetString() == "stripe");
        var description = stripe.GetProperty("description").GetString();
        Assert.Contains("Stripe__ConnectWebhookSecret", description);
        Assert.Contains("Stripe__SecretKey", description);
        Assert.DoesNotContain("whsec_", raw);
        Assert.DoesNotContain("pk_test_integration", raw);
    }

    [Fact]
    public async Task Ready_DatabaseUnreachable_Returns503UnhealthyWithoutConnectionDetails()
    {
        await using var factory = new UnreachableDatabaseFactory();
        using var client = factory.CreateClient();

        var ready = await client.GetAsync(HealthCheckExtensions.ReadyPath);
        var legacy = await client.GetAsync(HealthCheckExtensions.LegacyPath);
        var live = await client.GetAsync(HealthCheckExtensions.LivePath);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, ready.StatusCode);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, legacy.StatusCode);
        Assert.Equal(HttpStatusCode.OK, live.StatusCode);

        var raw = await ready.Content.ReadAsStringAsync();
        using var body = JsonDocument.Parse(raw);
        Assert.Equal("unhealthy", body.RootElement.GetProperty("status").GetString());
        Assert.Equal("unhealthy", ReadChecks(body)["database"]);
        foreach (var fragment in SensitiveFragments.Append("Npgsql").Append("refused").Append(UnreachableDatabaseFactory.UnreachableDatabaseName))
            Assert.DoesNotContain(fragment, raw, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage response)
    {
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    }

    private static Dictionary<string, string?> ReadChecks(JsonDocument body) =>
        body.RootElement.GetProperty("checks").EnumerateArray()
            .ToDictionary(c => c.GetProperty("name").GetString()!, c => c.GetProperty("status").GetString());

    /// <summary>Default factory with the commit variable Railway sets on GitHub deployments.</summary>
    public sealed class CommitShaFactory : CasazenWebApplicationFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureAppConfiguration((_, config) =>
                config.AddInMemoryCollection(new Dictionary<string, string?> { [BuildInfo.RailwayCommitVariable] = CommitSha }));
        }
    }

    /// <summary>The app's DbContext points at a PostgreSQL server that refuses connections.</summary>
    private sealed class UnreachableDatabaseFactory : CasazenWebApplicationFactory
    {
        public const string UnreachableDatabaseName = "fd12_unreachable";

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureTestServices(services =>
            {
                RemoveAllOf<DbContextOptions<AppDbContext>>(services);
                RemoveAllOf<IDbContextOptionsConfiguration<AppDbContext>>(services);
                services.AddCasazenDatabase(new ConfigurationBuilder()
                    .AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["ConnectionStrings:DefaultConnection"] =
                            $"Host=127.0.0.1;Port=1;Database={UnreachableDatabaseName};Username=postgres;Password=unused;Timeout=2",
                    })
                    .Build());
            });
        }
    }
}
