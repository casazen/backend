using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Casazen.Infrastructure.Data;
using Casazen.Web.Extensions;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Casazen.Tests.Integration;

/// <summary>
/// Client IP behind a trusted proxy and per-IP rate limiting (FD-10, #273: A1-12, A3-12, A3-30, A3-41, A5-10, A8-22,
/// A9-10). The proxy network is 10.0.0.0/8 with two trusted hops, every policy allows one request per window.
/// </summary>
public class ClientIpRateLimitingIntegrationTests : IClassFixture<ClientIpRateLimitingIntegrationTests.TrustedProxyFactory>
{
    private const string TrustedProxy = "10.0.0.5";
    private const string ConsentVersion = "2026-06-v1";

    private readonly TrustedProxyFactory _factory;

    public ClientIpRateLimitingIntegrationTests(TrustedProxyFactory factory) => _factory = factory;

    /// <summary>One anonymous endpoint per rate limiting policy (and every endpoint that got a new limiter).</summary>
    public static TheoryData<int, string, string, string?> LimitedEndpoints => new()
    {
        { 1, "GET", "/api/public/orgs/missing-org-fd10", null },
        { 2, "GET", "/api/public/orgs/missing-org-fd10/properties", null },
        { 3, "GET", "/api/properties/search?city=Nowhere", null },
        { 4, "GET", "/api/properties/0b6a1f0e-5c1d-4d7e-9a51-3f2c1e0a1001/public", null },
        { 5, "GET", "/api/public/bookings/property/0b6a1f0e-5c1d-4d7e-9a51-3f2c1e0a1002/availability", null },
        { 6, "POST", "/api/public/bookings/0b6a1f0e-5c1d-4d7e-9a51-3f2c1e0a1003/outcome", "{}" },
        { 7, "POST", "/api/public/bookings/lookup", "{}" },
        { 8, "POST", "/api/public/bookings", "{}" },
        { 9, "GET", "/api/public/checkin/missing-token", null },
        { 10, "POST", "/api/public/checkin/missing-token", "{}" },
        { 11, "POST", "/api/public/tourist-tax/calculate", "{}" },
        { 12, "GET", "/api/public/resolve-host?host=missing.casazen.it", null },
        { 13, "GET", "/api/public/ical/0b6a1f0e-5c1d-4d7e-9a51-3f2c1e0a1004", null },
        { 14, "POST", "/api/suppliers/register", "{}" },
        { 15, "POST", "/api/auth/register", "{}" },
        { 16, "GET", "/api/public/check-in/0b6a1f0e-5c1d-4d7e-9a51-3f2c1e0a1005?token=missing", null },
        { 17, "GET", "/api/public/suppliers/missing-supplier", null },
        { 18, "POST", "/api/suppliers/invites/lookup", "{}" },
        { 19, "GET", "/api/suppliers/registration-options", null },
    };

    [Theory]
    [MemberData(nameof(LimitedEndpoints))]
    public async Task AnonymousEndpoint_TwoClientIps_DoNotThrottleEachOther(int row, string method, string path, string? body)
    {
        using var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var firstClient = $"203.0.113.{row}";
        var secondClient = $"198.51.100.{row}";

        var first = await SendAsync(client, method, path, body, TrustedProxy, firstClient);
        Assert.NotEqual(HttpStatusCode.TooManyRequests, first.StatusCode);

        var repeated = await SendAsync(client, method, path, body, TrustedProxy, firstClient);
        await AssertRateLimitedAsync(repeated);

        var other = await SendAsync(client, method, path, body, TrustedProxy, secondClient);
        Assert.NotEqual(HttpStatusCode.TooManyRequests, other.StatusCode);
    }

    [Fact]
    public async Task GuestCheckInSubmit_SameIpDifferentToken_HasItsOwnQuota()
    {
        using var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        const string guest = "203.0.113.100";

        var first = await SendAsync(client, "POST", "/api/public/checkin/token-a", "{}", TrustedProxy, guest);
        Assert.NotEqual(HttpStatusCode.TooManyRequests, first.StatusCode);
        await AssertRateLimitedAsync(await SendAsync(client, "POST", "/api/public/checkin/token-a", "{}", TrustedProxy, guest));

        var otherToken = await SendAsync(client, "POST", "/api/public/checkin/token-b", "{}", TrustedProxy, guest);
        Assert.NotEqual(HttpStatusCode.TooManyRequests, otherToken.StatusCode);
    }

    [Fact]
    public async Task PublicBookingLookup_AfterCreateQuotaIsUsed_IsNotThrottled()
    {
        using var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        const string guest = "203.0.113.101";

        Assert.NotEqual(
            HttpStatusCode.TooManyRequests,
            (await SendAsync(client, "POST", "/api/public/bookings", "{}", TrustedProxy, guest)).StatusCode);
        await AssertRateLimitedAsync(await SendAsync(client, "POST", "/api/public/bookings", "{}", TrustedProxy, guest));

        var lookup = await SendAsync(client, "POST", "/api/public/bookings/lookup", "{}", TrustedProxy, guest);
        Assert.NotEqual(HttpStatusCode.TooManyRequests, lookup.StatusCode);
    }

    [Fact]
    public async Task ForwardedFor_FromUntrustedPeer_DoesNotChangeClientIp()
    {
        using var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        const string directClient = "192.0.2.50";

        var first = await SendAsync(client, "GET", ResolveHostPath, null, directClient, forwardedFor: "203.0.113.150");
        Assert.NotEqual(HttpStatusCode.TooManyRequests, first.StatusCode);

        // A new forged value does not open a new bucket: the client is still the TCP peer.
        var forged = await SendAsync(client, "GET", ResolveHostPath, null, directClient, forwardedFor: "203.0.113.151");
        await AssertRateLimitedAsync(forged);
    }

    [Fact]
    public async Task ForwardedFor_ForgedLeftmostBehindTrustedProxy_UsesAddressAppendedByProxy()
    {
        using var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var first = await SendAsync(client, "GET", ResolveHostPath, null, TrustedProxy, forwardedFor: "1.1.1.1, 203.0.113.160");
        Assert.NotEqual(HttpStatusCode.TooManyRequests, first.StatusCode);

        // Same real client, different forged first value (and a second trusted proxy hop): same bucket.
        var forged = await SendAsync(
            client, "GET", ResolveHostPath, null, TrustedProxy, forwardedFor: "2.2.2.2, 203.0.113.160, 10.1.2.3");
        await AssertRateLimitedAsync(forged);
    }

    [Fact]
    public async Task CompleteOnboarding_BehindTrustedProxy_RecordsRealClientIpAsConsentEvidence()
    {
        var records = await OnboardAsync(TrustedProxy, forwardedFor: "6.6.6.6, 203.0.113.170");

        Assert.Equal(5, records.Count);
        Assert.All(records, record => Assert.Equal("203.0.113.170", record.IpAddress));
    }

    [Fact]
    public async Task CompleteOnboarding_ForgedForwardedForFromUntrustedPeer_RecordsPeerIp()
    {
        var records = await OnboardAsync("192.0.2.60", forwardedFor: "6.6.6.6");

        Assert.Equal(5, records.Count);
        Assert.All(records, record => Assert.Equal("192.0.2.60", record.IpAddress));
    }

    [Fact]
    public async Task RateLimited_EnglishClient_GetsLocalizedProblemWithRetryAfter()
    {
        using var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        const string guest = "203.0.113.180";
        await SendAsync(client, "GET", ResolveHostPath, null, TrustedProxy, guest);

        using var request = BuildRequest("GET", ResolveHostPath, null, TrustedProxy, guest);
        request.Headers.AcceptLanguage.ParseAdd("en");
        var response = await client.SendAsync(request);

        var problem = await AssertRateLimitedAsync(response);
        Assert.Equal("Too many requests in a short time. Try again in 60 seconds.", problem.GetProperty("detail").GetString());
        Assert.Equal(60, problem.GetProperty("retryAfterSeconds").GetInt32());
    }

    private const string ResolveHostPath = "/api/public/resolve-host?host=missing.casazen.it";

    private async Task<List<Casazen.Core.Entities.ConsentRecord>> OnboardAsync(string peer, string forwardedFor)
    {
        var userId = $"auth0|fd10-consent-{Guid.NewGuid():N}";
        using var client = _factory.CreateAuthenticatedClient(userId, roles: string.Empty);
        client.DefaultRequestHeaders.Add(TestPeerIpStartupFilter.HeaderName, peer);
        client.DefaultRequestHeaders.Add("X-Forwarded-For", forwardedFor);

        var response = await client.PostAsJsonAsync("/api/users/onboarding", new
        {
            rentalType = "ShortTerm",
            consents = new
            {
                tosAccepted = true,
                tosVersion = ConsentVersion,
                privacyAccepted = true,
                privacyVersion = ConsentVersion,
                dpaAccepted = true,
                dpaVersion = ConsentVersion,
                subprocessorsAcknowledged = true,
                subprocessorsVersion = ConsentVersion,
                marketingOptIn = true,
            },
        });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.ConsentRecords.IgnoreQueryFilters().Where(c => c.UserId == userId).ToListAsync();
    }

    internal static async Task<JsonElement> AssertRateLimitedAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        Assert.Equal(ApiProblemDetails.ContentType, response.Content.Headers.ContentType?.MediaType);

        var retryAfter = response.Headers.RetryAfter?.Delta;
        Assert.NotNull(retryAfter);
        Assert.True(retryAfter > TimeSpan.Zero);

        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(ProblemCodes.RateLimited, problem.GetProperty("code").GetString());
        Assert.Equal(429, problem.GetProperty("status").GetInt32());
        Assert.False(string.IsNullOrWhiteSpace(problem.GetProperty("detail").GetString()));
        Assert.False(string.IsNullOrWhiteSpace(problem.GetProperty("traceId").GetString()));
        return problem;
    }

    internal static Task<HttpResponseMessage> SendAsync(
        HttpClient client,
        string method,
        string path,
        string? body,
        string peer,
        string? forwardedFor = null)
    {
        var request = BuildRequest(method, path, body, peer, forwardedFor);
        return client.SendAsync(request);
    }

    private static HttpRequestMessage BuildRequest(string method, string path, string? body, string peer, string? forwardedFor)
    {
        var request = new HttpRequestMessage(new HttpMethod(method), path);
        request.Headers.Add(TestPeerIpStartupFilter.HeaderName, peer);
        if (forwardedFor is not null)
            request.Headers.Add("X-Forwarded-For", forwardedFor);
        if (body is not null)
            request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        return request;
    }

    /// <summary>Proxy network 10.0.0.0/8 (up to two hops), one request per window on every policy.</summary>
    public sealed class TrustedProxyFactory : CasazenWebApplicationFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);

            var settings = new Dictionary<string, string?>
            {
                ["ForwardedHeaders:KnownNetworks"] = "10.0.0.0/8",
                ["ForwardedHeaders:ForwardLimit"] = "2",
            };
            foreach (var policy in RateLimitingServiceCollectionExtensions.Policies)
                settings[$"RateLimiting:{policy.Name}:PermitLimit"] = "1";

            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(settings));
            builder.ConfigureTestServices(services =>
                services.AddSingleton<Microsoft.AspNetCore.Hosting.IStartupFilter, TestPeerIpStartupFilter>());
        }
    }
}
