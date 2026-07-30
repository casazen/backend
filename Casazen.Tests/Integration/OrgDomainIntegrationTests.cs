using System.Net;
using System.Net.Http.Json;
using Casazen.Core.Entities.Enums;
using Casazen.Infrastructure.Data;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Casazen.Tests.Integration;

public class OrgDomainIntegrationTests : IClassFixture<CasazenWebApplicationFactory>
{
    private readonly CasazenWebApplicationFactory _factory;

    public OrgDomainIntegrationTests(CasazenWebApplicationFactory factory) => _factory = factory;

    [Fact]
    public async Task SetCustomDomain_OnStarter_Returns403()
    {
        var ownerId = $"auth0|starter-{Guid.NewGuid():N}";
        var org = await _factory.SeedOrgForOwnerAsync(ownerId);
        await SetPlanTierAsync(org.Id, PlanTier.Starter);

        using var client = _factory.CreateAuthenticatedClient(ownerId, "PropertyOwner");
        var response = await client.PostAsJsonAsync($"/api/orgs/{org.Id}/domain", new
        {
            hostMode = PublicHostMode.CustomDomain,
            customDomain = "www.starter-host.it",
        });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task SetCustomDomain_OnPro_Returns200WithDnsInstructions()
    {
        var ownerId = $"auth0|pro-{Guid.NewGuid():N}";
        var org = await _factory.SeedOrgForOwnerAsync(ownerId);
        await SetPlanTierAsync(org.Id, PlanTier.Pro);

        using var client = _factory.CreateAuthenticatedClient(ownerId, "PropertyOwner");
        var response = await client.PostAsJsonAsync($"/api/orgs/{org.Id}/domain", new
        {
            hostMode = PublicHostMode.CustomDomain,
            customDomain = "www.pro-host.it",
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        Assert.Equal("CustomDomain", json.GetProperty("publicHostMode").GetString());
        Assert.Equal("www.pro-host.it", json.GetProperty("customDomain").GetString());
        Assert.True(json.GetProperty("canUseCustomDomain").GetBoolean());
        Assert.False(string.IsNullOrWhiteSpace(json.GetProperty("dnsInstructions").GetProperty("txtValue").GetString()));
    }

    [Fact]
    public async Task SetCustomDomain_WhenOtherOrgClaimIsPending_AllowsOwnerToConfigureSameDomain()
    {
        var squatterId = $"auth0|domain-pending-{Guid.NewGuid():N}";
        var ownerId = $"auth0|domain-owner-{Guid.NewGuid():N}";
        var squatterOrg = await _factory.SeedOrgForOwnerAsync(squatterId);
        var ownerOrg = await _factory.SeedOrgForOwnerAsync(ownerId);
        await SetPlanTierAsync(squatterOrg.Id, PlanTier.Pro);
        await SetPlanTierAsync(ownerOrg.Id, PlanTier.Pro);

        using var squatterClient = _factory.CreateAuthenticatedClient(squatterId, "PropertyOwner");
        var pendingResponse = await squatterClient.PostAsJsonAsync($"/api/orgs/{squatterOrg.Id}/domain", new
        {
            hostMode = PublicHostMode.CustomDomain,
            customDomain = "www.pending-claim.it",
        });
        Assert.Equal(HttpStatusCode.OK, pendingResponse.StatusCode);

        using var ownerClient = _factory.CreateAuthenticatedClient(ownerId, "PropertyOwner");
        var ownerResponse = await ownerClient.PostAsJsonAsync($"/api/orgs/{ownerOrg.Id}/domain", new
        {
            hostMode = PublicHostMode.CustomDomain,
            customDomain = "www.pending-claim.it",
        });

        Assert.Equal(HttpStatusCode.OK, ownerResponse.StatusCode);
        var json = await ownerResponse.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        Assert.Equal(ownerOrg.Id, json.GetProperty("orgId").GetGuid());
        Assert.Equal("www.pending-claim.it", json.GetProperty("customDomain").GetString());
        Assert.Equal("Pending", json.GetProperty("domainVerificationStatus").GetString());
    }

    [Fact]
    public async Task SetDomain_ForAnotherOrg_Returns403()
    {
        var ownerId = $"auth0|owner-{Guid.NewGuid():N}";
        var attackerId = $"auth0|attacker-{Guid.NewGuid():N}";
        var org = await _factory.SeedOrgForOwnerAsync(ownerId);
        await _factory.SeedOrgForOwnerAsync(attackerId);

        using var client = _factory.CreateAuthenticatedClient(attackerId, "PropertyOwner");
        var response = await client.PostAsJsonAsync($"/api/orgs/{org.Id}/domain", new
        {
            hostMode = PublicHostMode.CasazenSubdomain,
            subdomain = "attacker-slug",
        });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task ResolveHost_VerifiedCustomDomain_ReturnsBranding()
    {
        var ownerId = $"auth0|resolve-{Guid.NewGuid():N}";
        var org = await _factory.SeedOrgForOwnerAsync(ownerId);
        await SetPlanTierAsync(org.Id, PlanTier.Pro);
        await SetVerifiedCustomDomainAsync(org.Id, "www.verified-host.it");

        using var client = _factory.CreateClient();
        var response = await client.GetAsync("/api/public/resolve-host?host=www.verified-host.it");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        Assert.Equal(org.Id, json.GetProperty("orgId").GetGuid());
        Assert.Equal("CustomDomain", json.GetProperty("publicHostMode").GetString());
        Assert.Equal(org.Slug, json.GetProperty("slug").GetString());
    }

    private async Task SetPlanTierAsync(Guid orgId, PlanTier tier)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var org = await db.Orgs.FindAsync(orgId);
        org!.PlanTier = tier;
        await db.SaveChangesAsync();
    }

    private async Task SetVerifiedCustomDomainAsync(Guid orgId, string customDomain)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var org = await db.Orgs.FindAsync(orgId);
        org!.PublicHostMode = PublicHostMode.CustomDomain;
        org.CustomDomain = customDomain;
        org.DomainVerificationStatus = DomainVerificationStatus.Verified;
        org.DomainVerificationToken = "verify-token";
        await db.SaveChangesAsync();
    }
}
