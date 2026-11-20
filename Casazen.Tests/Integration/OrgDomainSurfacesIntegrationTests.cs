using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Casazen.Core.Entities.Enums;
using Casazen.Infrastructure.Data;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Casazen.Tests.Integration;

/// <summary>
/// E40/F01/US01/T01 (NF-140..NF-142): the surfaces of the agency's own domain in the domain settings. The endpoint is hidden while
/// <c>WhiteLabelHost</c> is off, saves only the caller's own org, follows the domain's plan, and a save is seen by <c>resolve-host</c> at once.
/// </summary>
public class OrgDomainSurfacesIntegrationTests(CasazenWebApplicationFactory flagsOff, SiteIntegrationFlagsFactory flagsOn)
    : IClassFixture<CasazenWebApplicationFactory>, IClassFixture<SiteIntegrationFlagsFactory>
{
    private static readonly object AllOn = new { guestAreaOnOwnDomain = true, ownerPortalOnOwnDomain = true, useOwnDomainForLinks = true };

    [Fact]
    public async Task PutSurfaces_FlagOff_NotFound()
    {
        var ownerId = $"auth0|surfaces-off-{Guid.NewGuid():N}";
        var org = await flagsOff.SeedOrgForOwnerAsync(ownerId);
        await SetPlanAsync(flagsOff, org.Id, PlanTier.Pro);
        await SetVerifiedDomainAsync(flagsOff, org.Id, $"off-{Guid.NewGuid():N}.example.it");

        using var client = flagsOff.CreateAuthenticatedClient(ownerId, "PropertyOwner");
        var response = await client.PutAsJsonAsync($"/api/orgs/{org.Id}/domain/surfaces", AllOn);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.False((await ReadOrgAsync(flagsOff, org.Id)).GuestAreaOnOwnDomain);
    }

    [Fact]
    public async Task GetDomain_FlagOff_SurfacesNull()
    {
        var ownerId = $"auth0|surfaces-get-off-{Guid.NewGuid():N}";
        var org = await flagsOff.SeedOrgForOwnerAsync(ownerId);

        using var client = flagsOff.CreateAuthenticatedClient(ownerId, "PropertyOwner");
        var json = await client.GetFromJsonAsync<JsonElement>($"/api/orgs/{org.Id}/domain");

        Assert.Equal(JsonValueKind.Null, json.GetProperty("surfaces").ValueKind);
    }

    [Fact]
    public async Task PutSurfaces_OtherOrgId_Forbidden()
    {
        var victimId = $"auth0|surfaces-victim-{Guid.NewGuid():N}";
        var attackerId = $"auth0|surfaces-attacker-{Guid.NewGuid():N}";
        var victim = await flagsOn.SeedOrgForOwnerAsync(victimId);
        var attacker = await flagsOn.SeedOrgForOwnerAsync(attackerId);
        await SetPlanAsync(flagsOn, victim.Id, PlanTier.Pro);
        await SetPlanAsync(flagsOn, attacker.Id, PlanTier.Pro);
        await SetVerifiedDomainAsync(flagsOn, victim.Id, $"victim-{Guid.NewGuid():N}.example.it");

        using var client = flagsOn.CreateAuthenticatedClient(attackerId, "PropertyOwner");
        var response = await client.PutAsJsonAsync($"/api/orgs/{victim.Id}/domain/surfaces", AllOn);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var stored = await ReadOrgAsync(flagsOn, victim.Id);
        Assert.False(stored.GuestAreaOnOwnDomain);
        Assert.False(stored.OwnerPortalOnOwnDomain);
        Assert.Equal(OwnerLoginHostState.NotRequested, stored.OwnerLoginHostState);
    }

    [Fact]
    public async Task PutSurfaces_WithoutPlan_PlanRequired()
    {
        var ownerId = $"auth0|surfaces-starter-{Guid.NewGuid():N}";
        var org = await flagsOn.SeedOrgForOwnerAsync(ownerId);
        await SetPlanAsync(flagsOn, org.Id, PlanTier.Starter);

        using var client = flagsOn.CreateAuthenticatedClient(ownerId, "PropertyOwner");
        var response = await client.PutAsJsonAsync($"/api/orgs/{org.Id}/domain/surfaces", AllOn);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("plan_required", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
        Assert.False((await ReadOrgAsync(flagsOn, org.Id)).GuestAreaOnOwnDomain);
    }

    [Fact]
    public async Task PutSurfaces_WithoutCustomDomain_NotConfigured()
    {
        var ownerId = $"auth0|surfaces-nodomain-{Guid.NewGuid():N}";
        var org = await flagsOn.SeedOrgForOwnerAsync(ownerId);
        await SetPlanAsync(flagsOn, org.Id, PlanTier.Pro);

        using var client = flagsOn.CreateAuthenticatedClient(ownerId, "PropertyOwner");
        var response = await client.PutAsJsonAsync($"/api/orgs/{org.Id}/domain/surfaces", AllOn);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("org_domain_not_configured", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
    }

    [Fact]
    public async Task PutSurfaces_MissingChoice_BadRequest()
    {
        var ownerId = $"auth0|surfaces-missing-{Guid.NewGuid():N}";
        var org = await flagsOn.SeedOrgForOwnerAsync(ownerId);
        await SetPlanAsync(flagsOn, org.Id, PlanTier.Pro);
        await SetVerifiedDomainAsync(flagsOn, org.Id, $"missing-{Guid.NewGuid():N}.example.it");

        using var client = flagsOn.CreateAuthenticatedClient(ownerId, "PropertyOwner");
        var response = await client.PutAsJsonAsync($"/api/orgs/{org.Id}/domain/surfaces", new { guestAreaOnOwnDomain = true });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.False((await ReadOrgAsync(flagsOn, org.Id)).GuestAreaOnOwnDomain);
    }

    [Fact]
    public async Task PutSurfaces_SavesTheChoices_AndResolveHostServesThemAtOnce()
    {
        var ownerId = $"auth0|surfaces-save-{Guid.NewGuid():N}";
        var domain = $"save-{Guid.NewGuid():N}.example.it";
        var org = await flagsOn.SeedOrgForOwnerAsync(ownerId);
        await SetPlanAsync(flagsOn, org.Id, PlanTier.Pro);
        await SetVerifiedDomainAsync(flagsOn, org.Id, domain);
        using var publicClient = flagsOn.CreateClient();
        var before = await publicClient.GetFromJsonAsync<JsonElement>($"/api/public/resolve-host?host={domain}");
        Assert.False(before.GetProperty("surfaces").GetProperty("guestArea").GetBoolean());

        using var client = flagsOn.CreateAuthenticatedClient(ownerId, "PropertyOwner");
        var response = await client.PutAsJsonAsync($"/api/orgs/{org.Id}/domain/surfaces", AllOn);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var config = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(domain, config.GetProperty("customDomain").GetString());
        var surfaces = config.GetProperty("surfaces");
        Assert.True(surfaces.GetProperty("guestAreaOnOwnDomain").GetBoolean());
        Assert.True(surfaces.GetProperty("ownerPortalOnOwnDomain").GetBoolean());
        Assert.True(surfaces.GetProperty("useOwnDomainForLinks").GetBoolean());
        // The owner portal asks for the Auth0 registration of the domain; until it is ready no owner signs in there.
        Assert.Equal("Pending", surfaces.GetProperty("ownerLoginHostState").GetString());
        Assert.False(surfaces.GetProperty("ownerLoginAutomatic").GetBoolean());
        var effective = surfaces.GetProperty("effective");
        Assert.True(effective.GetProperty("guestArea").GetBoolean());
        Assert.True(effective.GetProperty("ownerPortal").GetBoolean());
        Assert.False(effective.GetProperty("ownerLoginReady").GetBoolean());
        Assert.True(effective.GetProperty("linksOnOwnDomain").GetBoolean());

        var after = await publicClient.GetFromJsonAsync<JsonElement>($"/api/public/resolve-host?host={domain}");
        Assert.True(after.GetProperty("surfaces").GetProperty("guestArea").GetBoolean());
        Assert.True(after.GetProperty("surfaces").GetProperty("ownerPortal").GetBoolean());
        Assert.False(after.GetProperty("ownerLoginReady").GetBoolean());
        Assert.Equal(org.Id, after.GetProperty("orgId").GetGuid());
    }

    [Fact]
    public async Task SetDomain_AnotherCustomDomain_ForgetsTheRegistration_AsksAgainForTheNewDomain_KeepsTheChoices()
    {
        var ownerId = $"auth0|surfaces-reset-{Guid.NewGuid():N}";
        var org = await flagsOn.SeedOrgForOwnerAsync(ownerId);
        await SetPlanAsync(flagsOn, org.Id, PlanTier.Pro);
        await SetVerifiedDomainAsync(flagsOn, org.Id, $"first-{Guid.NewGuid():N}.example.it");
        await UpdateOrgAsync(flagsOn, org.Id, o =>
        {
            o.GuestAreaOnOwnDomain = true;
            o.OwnerPortalOnOwnDomain = true;
            o.UseOwnDomainForLinks = false;
            o.OwnerLoginHostState = OwnerLoginHostState.Ready;
        });

        using var client = flagsOn.CreateAuthenticatedClient(ownerId, "PropertyOwner");
        var response = await client.PostAsJsonAsync($"/api/orgs/{org.Id}/domain", new
        {
            hostMode = PublicHostMode.CustomDomain,
            customDomain = $"second-{Guid.NewGuid():N}.example.it",
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var stored = await ReadOrgAsync(flagsOn, org.Id);
        // The registration of the first domain is forgotten and, the owner portal being on, the new domain asks for its own at once.
        Assert.Equal(OwnerLoginHostState.Pending, stored.OwnerLoginHostState);
        Assert.True(stored.GuestAreaOnOwnDomain);
        Assert.True(stored.OwnerPortalOnOwnDomain);
        Assert.False(stored.UseOwnDomainForLinks);
    }

    [Fact]
    public async Task SetDomain_SwitchToThePlatformPath_ResetsOwnerLoginState()
    {
        var ownerId = $"auth0|surfaces-path-{Guid.NewGuid():N}";
        var org = await flagsOn.SeedOrgForOwnerAsync(ownerId);
        await SetPlanAsync(flagsOn, org.Id, PlanTier.Pro);
        await SetVerifiedDomainAsync(flagsOn, org.Id, $"path-{Guid.NewGuid():N}.example.it");
        await UpdateOrgAsync(flagsOn, org.Id, o =>
        {
            o.OwnerPortalOnOwnDomain = true;
            o.OwnerLoginHostState = OwnerLoginHostState.Ready;
        });

        using var client = flagsOn.CreateAuthenticatedClient(ownerId, "PropertyOwner");
        var response = await client.PostAsJsonAsync($"/api/orgs/{org.Id}/domain", new { hostMode = PublicHostMode.CasazenPath });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var stored = await ReadOrgAsync(flagsOn, org.Id);
        Assert.Equal(OwnerLoginHostState.NotRequested, stored.OwnerLoginHostState);
        Assert.True(stored.OwnerPortalOnOwnDomain);
    }

    /// <summary>Pro/Scale take effect only while a subscription pays for them (#274), like <c>OrgDomainIntegrationTests</c>.</summary>
    internal static Task SetPlanAsync(CasazenWebApplicationFactory factory, Guid orgId, PlanTier tier) =>
        UpdateOrgAsync(factory, orgId, org =>
        {
            var paid = tier != PlanTier.Starter;
            org.PlanTier = tier;
            org.SubscriptionId = paid ? $"sub_test_{orgId:N}" : null;
            org.SubscriptionStatus = paid ? SubscriptionStatus.Active : SubscriptionStatus.None;
        });

    internal static Task SetVerifiedDomainAsync(CasazenWebApplicationFactory factory, Guid orgId, string domain) =>
        UpdateOrgAsync(factory, orgId, org =>
        {
            org.PublicHostMode = PublicHostMode.CustomDomain;
            org.CustomDomain = domain;
            org.DomainVerificationStatus = DomainVerificationStatus.Verified;
            org.DomainVerificationToken = "verify-token";
        });

    internal static async Task UpdateOrgAsync(CasazenWebApplicationFactory factory, Guid orgId, Action<OrgEntity> change)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var org = await db.Orgs.FindAsync(orgId);
        change(org!);
        await db.SaveChangesAsync();
    }

    internal static async Task<OrgEntity> ReadOrgAsync(CasazenWebApplicationFactory factory, Guid orgId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var org = await db.Orgs.FindAsync(orgId);
        return org!;
    }
}

/// <summary>
/// The default integration factory with the flags of the site integration on (<c>WhiteLabelHost</c> and <c>SiteEmbed</c>, E40-E43);
/// <c>Auth0HostSync</c> stays off, as it will in the test environment until the Management credentials exist.
/// </summary>
public sealed class SiteIntegrationFlagsFactory : CasazenWebApplicationFactory
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureAppConfiguration((_, config) =>
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Features:WhiteLabelHost"] = "true",
                ["Features:SiteEmbed"] = "true",
            }));
    }
}
