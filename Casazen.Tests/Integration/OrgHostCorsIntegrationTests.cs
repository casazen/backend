using System.Net;
using Casazen.Core.Entities.Enums;
using Casazen.Infrastructure.Data;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Casazen.Tests.Integration;

/// <summary>
/// BK-16 (A3-08): the browser on an org's own site (subdomain or custom domain) may call the public API, and nobody else.
/// The factory configures <c>PublicHost:BaseDomain = casazen.it</c> and a public site on another domain.
/// </summary>
public class OrgHostCorsIntegrationTests : IClassFixture<CasazenWebApplicationFactory>
{
    private const string PublicApi = "/api/public/orgs/anything";
    private readonly CasazenWebApplicationFactory _factory;

    public OrgHostCorsIntegrationTests(CasazenWebApplicationFactory factory) => _factory = factory;

    [Fact]
    public async Task Preflight_VerifiedCustomDomainOnAPaidPlan_IsAllowedWithItsOwnOriginAndWithoutCredentials()
    {
        var domain = $"www.{Guid.NewGuid():N}.example.test";
        await SeedOrgAsync(PlanTier.Pro, customDomain: domain, verified: true);

        using var response = await PreflightAsync($"https://{domain}", PublicApi);

        Assert.Equal($"https://{domain}", Assert.Single(response.Headers.GetValues("Access-Control-Allow-Origin")));
        Assert.False(response.Headers.Contains("Access-Control-Allow-Credentials"));
        Assert.Contains("Origin", response.Headers.Vary);
    }

    [Fact]
    public async Task Preflight_SubdomainOfAnOrgThatChoseTheSubdomainMode_IsAllowed()
    {
        var label = $"v{Guid.NewGuid():N}"[..20];
        await SeedOrgAsync(PlanTier.Starter, subdomain: label);

        using var response = await PreflightAsync($"https://{label}.casazen.it", PublicApi);

        Assert.Equal($"https://{label}.casazen.it", Assert.Single(response.Headers.GetValues("Access-Control-Allow-Origin")));
    }

    [Fact]
    public async Task Preflight_CustomDomainWaitingForItsVerification_IsRejected()
    {
        var domain = $"www.{Guid.NewGuid():N}.example.test";
        await SeedOrgAsync(PlanTier.Pro, customDomain: domain, verified: false);

        using var response = await PreflightAsync($"https://{domain}", PublicApi);

        Assert.False(response.Headers.Contains("Access-Control-Allow-Origin"));
    }

    [Fact]
    public async Task Preflight_VerifiedCustomDomainWithoutAPaidProPlan_IsRejected()
    {
        // A Pro tier nobody pays for is Starter (A3-07): the custom domain is a Pro feature, so it stops being served.
        var domain = $"www.{Guid.NewGuid():N}.example.test";
        await SeedOrgAsync(PlanTier.Pro, paid: false, customDomain: domain, verified: true);

        using var response = await PreflightAsync($"https://{domain}", PublicApi);

        Assert.False(response.Headers.Contains("Access-Control-Allow-Origin"));
    }

    [Theory]
    [InlineData("https://nobody.casazen.it")]
    [InlineData("https://www.casazen.it")]
    [InlineData("https://api.casazen.it")]
    [InlineData("https://casazen.it")]
    [InlineData("https://a.b.casazen.it")]
    [InlineData("https://unknown-host.example.test")]
    [InlineData("https://casazen.it.evil.test")]
    [InlineData("http://nobody.casazen.it")]
    [InlineData("https://nobody.casazen.it:8443")]
    [InlineData("https://user@nobody.casazen.it")]
    [InlineData("https://127.0.0.1")]
    [InlineData("null")]
    public async Task Preflight_NoWildcardOnTheBaseDomain_UnknownReservedOrMalformedOrigins_AreRejected(string origin)
    {
        using var response = await PreflightAsync(origin, PublicApi);

        Assert.False(response.Headers.Contains("Access-Control-Allow-Origin"));
    }

    [Fact]
    public async Task Preflight_SubdomainOfAnOrgOnThePathMode_IsRejected()
    {
        // The slug of an org that did not choose the subdomain mode is not a host of the platform.
        var org = await SeedOrgAsync(PlanTier.Starter);

        using var response = await PreflightAsync($"https://{org.Slug}.casazen.it", PublicApi);

        Assert.False(response.Headers.Contains("Access-Control-Allow-Origin"));
    }

    [Theory]
    [InlineData("/api/properties")]
    [InlineData("/api/users/me")]
    [InlineData("/api/orgs/me")]
    [InlineData("/api/bookings")]
    [InlineData("/api/admin/users")]
    public async Task Preflight_OrgHostOrigin_IsRejectedOnEndpointsThatAreNotPublic(string path)
    {
        var domain = $"www.{Guid.NewGuid():N}.example.test";
        await SeedOrgAsync(PlanTier.Pro, customDomain: domain, verified: true);

        using var response = await PreflightAsync($"https://{domain}", path);

        Assert.False(response.Headers.Contains("Access-Control-Allow-Origin"));
    }

    [Fact]
    public async Task Preflight_OrgHostOrigin_IsAllowedOnTheLegalDocumentsToo()
    {
        var domain = $"www.{Guid.NewGuid():N}.example.test";
        await SeedOrgAsync(PlanTier.Pro, customDomain: domain, verified: true);

        using var response = await PreflightAsync($"https://{domain}", "/api/legal/privacy");

        Assert.Equal($"https://{domain}", Assert.Single(response.Headers.GetValues("Access-Control-Allow-Origin")));
    }

    [Fact]
    public async Task Preflight_DomainVerifiedAfterARejection_IsAllowedOnceTheCacheIsInvalidated()
    {
        var domain = $"www.{Guid.NewGuid():N}.example.test";
        var org = await SeedOrgAsync(PlanTier.Pro, customDomain: domain, verified: false);
        using var rejected = await PreflightAsync($"https://{domain}", PublicApi);
        Assert.False(rejected.Headers.Contains("Access-Control-Allow-Origin"));

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var stored = await db.Orgs.FindAsync(org.Id);
            stored!.DomainVerificationStatus = DomainVerificationStatus.Verified;
            await db.SaveChangesAsync();
            // What OrgDomainService.VerifyDomainAsync does after a successful verification.
            scope.ServiceProvider.GetRequiredService<Casazen.Core.Services.IPublicHostResolver>().InvalidateCacheForHost(domain);
        }

        using var allowed = await PreflightAsync($"https://{domain}", PublicApi);

        Assert.Equal($"https://{domain}", Assert.Single(allowed.Headers.GetValues("Access-Control-Allow-Origin")));
    }

    [Fact]
    public async Task Get_RequestFromAnOrgHost_ReceivesTheAnswerWithItsOriginAndNeverAWildcard()
    {
        var domain = $"www.{Guid.NewGuid():N}.example.test";
        await SeedOrgAsync(PlanTier.Pro, customDomain: domain, verified: true);
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/public/resolve-host?host=" + domain);
        request.Headers.Add("Origin", $"https://{domain}");

        using var response = await _factory.CreateClient().SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal($"https://{domain}", Assert.Single(response.Headers.GetValues("Access-Control-Allow-Origin")));
    }

    private async Task<HttpResponseMessage> PreflightAsync(string origin, string path)
    {
        using var request = new HttpRequestMessage(HttpMethod.Options, path);
        request.Headers.Add("Origin", origin);
        request.Headers.Add("Access-Control-Request-Method", "GET");
        return await _factory.CreateClient().SendAsync(request);
    }

    private async Task<OrgEntity> SeedOrgAsync(
        PlanTier planTier,
        bool paid = true,
        string? customDomain = null,
        bool verified = false,
        string? subdomain = null)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var withSubscription = planTier != PlanTier.Starter && paid;
        var org = new OrgEntity
        {
            Name = "Host",
            DisplayName = "Host",
            Slug = $"cors-{Guid.NewGuid():N}",
            ContactEmail = "host@example.test",
            PlanTier = planTier,
            SubscriptionId = withSubscription ? $"sub_test_{Guid.NewGuid():N}" : null,
            SubscriptionStatus = withSubscription ? SubscriptionStatus.Active : SubscriptionStatus.None,
            IsActive = true,
            PublicHostMode = customDomain is not null
                ? PublicHostMode.CustomDomain
                : subdomain is not null ? PublicHostMode.CasazenSubdomain : PublicHostMode.CasazenPath,
            CustomDomain = customDomain,
            Subdomain = subdomain,
            DomainVerificationStatus = verified ? DomainVerificationStatus.Verified : DomainVerificationStatus.Pending,
            DomainVerificationToken = customDomain is null ? null : "token",
        };
        db.Orgs.Add(org);
        await db.SaveChangesAsync();
        return org;
    }
}
