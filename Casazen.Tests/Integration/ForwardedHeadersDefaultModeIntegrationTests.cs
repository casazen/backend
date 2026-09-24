using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Casazen.Tests.Integration;

/// <summary>
/// Default configuration (no <c>ForwardedHeaders:KnownNetworks</c>): the peer is trusted as the proxy for one hop, so
/// the client IP is the address the proxy appended last, never the client-written first value (FD-10, A1-12, A3-30).
/// </summary>
public class ForwardedHeadersDefaultModeIntegrationTests
    : IClassFixture<ForwardedHeadersDefaultModeIntegrationTests.DefaultModeFactory>
{
    private const string ProxyPeer = "172.16.0.1";
    private const string ResolveHostPath = "/api/public/resolve-host?host=missing.casazen.it";

    private readonly DefaultModeFactory _factory;

    public ForwardedHeadersDefaultModeIntegrationTests(DefaultModeFactory factory) => _factory = factory;

    [Fact]
    public async Task GetClientIp_NoKnownNetworks_UsesLastForwardedHopAndIgnoresForgedFirstValue()
    {
        using var client = _factory.CreateAuthenticatedClient($"auth0|fd10-admin-{Guid.NewGuid():N}", roles: "Admin");
        client.DefaultRequestHeaders.Add(TestPeerIpStartupFilter.HeaderName, ProxyPeer);
        client.DefaultRequestHeaders.Add("X-Forwarded-For", "6.6.6.6, 203.0.113.88");
        client.DefaultRequestHeaders.Add("X-Forwarded-Proto", "https");

        var response = await client.GetAsync("/api/admin/diagnostics/client-ip");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("203.0.113.88", body.GetProperty("clientIp").GetString());
        Assert.Equal("203.0.113.88", body.GetProperty("rateLimitKey").GetString());
        Assert.StartsWith(ProxyPeer, body.GetProperty("originalPeer").GetString());
        Assert.Equal(["6.6.6.6"], body.GetProperty("unprocessedForwardedFor").EnumerateArray().Select(e => e.GetString()));
        Assert.Equal("https", body.GetProperty("scheme").GetString());
        Assert.Equal(1, body.GetProperty("forwardLimit").GetInt32());
        Assert.Equal(0, body.GetProperty("knownNetworks").GetArrayLength());
    }

    [Fact]
    public async Task GetClientIp_NonAdmin_ReturnsForbidden()
    {
        using var client = _factory.CreateAuthenticatedClient($"auth0|fd10-host-{Guid.NewGuid():N}", roles: "PropertyManager");

        var response = await client.GetAsync("/api/admin/diagnostics/client-ip");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task ResolveHost_NoKnownNetworks_ForgedFirstValueDoesNotOpenNewBucket()
    {
        using var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var first = await ClientIpRateLimitingIntegrationTests.SendAsync(
            client, "GET", ResolveHostPath, null, ProxyPeer, forwardedFor: "1.1.1.1, 203.0.113.89");
        Assert.NotEqual(HttpStatusCode.TooManyRequests, first.StatusCode);

        var forged = await ClientIpRateLimitingIntegrationTests.SendAsync(
            client, "GET", ResolveHostPath, null, ProxyPeer, forwardedFor: "9.9.9.9, 203.0.113.89");
        await ClientIpRateLimitingIntegrationTests.AssertRateLimitedAsync(forged);

        var otherClient = await ClientIpRateLimitingIntegrationTests.SendAsync(
            client, "GET", ResolveHostPath, null, ProxyPeer, forwardedFor: "203.0.113.90");
        Assert.NotEqual(HttpStatusCode.TooManyRequests, otherClient.StatusCode);
    }

    /// <summary>Application defaults for forwarded headers; resolve-host allows one request per window.</summary>
    public sealed class DefaultModeFactory : CasazenWebApplicationFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);

            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["PublicHost:RateLimitPermitLimit"] = "1",
            }));
            builder.ConfigureTestServices(services =>
                services.AddSingleton<IStartupFilter, TestPeerIpStartupFilter>());
        }
    }
}
