using System.Net;
using System.Text;
using Casazen.Core.Multitenancy;
using Casazen.Core.Services;
using Casazen.Infrastructure.Octorate;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Casazen.Tests.Unit.Octorate;

/// <summary>
/// The proofs of the Octorate client (E02/F01/US01/T02): every call acts for the org of <see cref="IOctorateOrgScope"/>, with that org's
/// token and bucket and never another's, and no call is made for no org (ADR-023-R4, R15); the rate limit is per org (ADR-023-R8, R14).
/// The HTTP answer is a stub: what is proven is which token and which bucket a call uses, not Octorate.
/// </summary>
public class OctorateApiClientTests
{
    private static readonly Guid OrgA = Guid.Parse("0f5b8f0e-1a7e-4c55-9c38-3b0a2f6c1a01");
    private static readonly Guid OrgB = Guid.Parse("7d2c4e91-58b3-4f0a-a6e2-9c1d0b3e4f02");

    [Fact]
    public async Task OctorateClient_OrgScope_NeverUsesAnotherOrgsToken_Test()
    {
        var h = new Harness();

        using (h.Scope.Use(OrgA))
            await h.Client.GetConfigurationAsync(CancellationToken.None);
        using (h.Scope.Use(OrgB))
            await h.Client.GetPropertyAsync("4711", CancellationToken.None);

        // Each call asked the token of its own org and sent it, and took a call from its own org's bucket only.
        Assert.Equal(new[] { OrgA, OrgB }, h.Tokens.RequestedOrgs);
        Assert.Equal(new[] { "Bearer " + FakeTokens.TokenOf(OrgA), "Bearer " + FakeTokens.TokenOf(OrgB) }, h.Handler.Authorizations);
        Assert.Equal(OctorateRateLimiter.RequestsPerMinute - 1, h.Limiter.AvailableCalls(OrgA));
        Assert.Equal(OctorateRateLimiter.RequestsPerMinute - 1, h.Limiter.AvailableCalls(OrgB));

        // No org (a job that forgot Use): refused before any token is read and before anything leaves.
        await Assert.ThrowsAsync<InvalidOperationException>(() => h.Client.GetConfigurationAsync(CancellationToken.None));
        Assert.Equal(2, h.Tokens.RequestedOrgs.Count);
        Assert.Equal(2, h.Handler.Authorizations.Count);

        // Inside an authenticated request of org A the scope is org A, and it cannot be switched to org B.
        var requestScope = new OctorateOrgScope(new RequestTenant(OrgA));
        Assert.Equal(OrgA, requestScope.OrgId);
        Assert.Throws<InvalidOperationException>(() => requestScope.Use(OrgB));
        using (requestScope.Use(OrgA))
            Assert.Equal(OrgA, requestScope.OrgId);
    }

    [Fact]
    public async Task OctorateConnector_RateLimit_EnforcedPerOrg_Test()
    {
        var h = new Harness();

        using (h.Scope.Use(OrgA))
        {
            for (var i = 0; i < OctorateRateLimiter.RequestsPerMinute; i++)
                await h.Client.GetConfigurationAsync(CancellationToken.None);

            // The 101st call of the minute is refused by CasaZen itself: it never reaches Octorate.
            var refused = await Assert.ThrowsAsync<OctorateRateLimitException>(
                () => h.Client.GetConfigurationAsync(CancellationToken.None));
            Assert.Null(refused.OctorateStatusCode);
            Assert.Equal("octorate_rate_limited", refused.Code);
        }

        Assert.Equal(OctorateRateLimiter.RequestsPerMinute, h.Handler.Authorizations.Count);

        // Org B, in the same minute, has its own bucket: unaffected.
        using (h.Scope.Use(OrgB))
            await h.Client.GetConfigurationAsync(CancellationToken.None);

        Assert.Equal(OctorateRateLimiter.RequestsPerMinute + 1, h.Handler.Authorizations.Count);
        Assert.Equal(0, h.Limiter.AvailableCalls(OrgA));
        Assert.Equal(OctorateRateLimiter.RequestsPerMinute - 1, h.Limiter.AvailableCalls(OrgB));
    }

    private sealed class Harness
    {
        public Harness()
        {
            var clock = new FixedTimeProvider(new DateTimeOffset(2026, 11, 5, 10, 0, 0, TimeSpan.Zero));
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["Octorate:ApiBaseUrl"] = "https://api.octorate.test/connect" })
                .Build();
            Limiter = new OctorateRateLimiter(clock);
            Scope = new OctorateOrgScope(NullTenantContext.Instance);
            Client = new OctorateApiClient(
                new HttpClient(Handler),
                Tokens,
                Limiter,
                Scope,
                configuration,
                clock,
                NullLogger<OctorateApiClient>.Instance);
        }

        public FakeTokens Tokens { get; } = new();

        public RecordingHandler Handler { get; } = new();

        public OctorateRateLimiter Limiter { get; }

        public OctorateOrgScope Scope { get; }

        public OctorateApiClient Client { get; }
    }

    /// <summary>A token per org, and the orgs whose token was asked.</summary>
    private sealed class FakeTokens : IOctorateTokenService
    {
        public List<Guid> RequestedOrgs { get; } = [];

        public static string TokenOf(Guid orgId) => "token-" + orgId.ToString("N");

        public Task<string> GetAccessTokenAsync(Guid orgId, CancellationToken ct)
        {
            RequestedOrgs.Add(orgId);
            return Task.FromResult(TokenOf(orgId));
        }

        public Task<string> RefreshRejectedTokenAsync(Guid orgId, string rejectedAccessToken, CancellationToken ct) =>
            throw new InvalidOperationException("No token is refused in these tests.");

        public Task SaveTokensAsync(Guid orgId, OctorateTokenResponse tokens, CancellationToken ct) => Task.CompletedTask;

        public Task DisconnectAsync(Guid orgId, CancellationToken ct) => Task.CompletedTask;
    }

    /// <summary>Answers every call with one JSON object (configuration and accommodation both read what they need) and keeps the tokens sent.</summary>
    private sealed class RecordingHandler : HttpMessageHandler
    {
        private const string Body = """{"id":"4711","name":"Casa Prova","environment":"SANDBOX","quotaLimit":180,"quotaStatus":57}""";

        public List<string> Authorizations { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Authorizations.Add(request.Headers.Authorization?.ToString() ?? "(none)");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(Body, Encoding.UTF8, "application/json"),
            });
        }
    }

    /// <summary>The tenant of an authenticated request of one org.</summary>
    private sealed class RequestTenant(Guid orgId) : ITenantContext
    {
        public Guid? OrgId { get; } = orgId;

        public bool FilterEnabled => true;
    }
}
