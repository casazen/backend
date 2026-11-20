using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Casazen.Core.Entities.Enums;
using Xunit;
using static Casazen.Tests.Integration.OrgDomainSurfacesIntegrationTests;

namespace Casazen.Tests.Integration;

/// <summary>
/// E40/F01/US02/T01 (NF-141): over HTTP, the agency's retry of the owner sign-in on its own domain (<c>POST api/orgs/{orgId}/domain/owner-login</c>) and the
/// queue and the hand of the platform administrators (<c>api/admin/owner-login-hosts</c>, <c>api/admin/orgs/{orgId}/owner-login-state</c>). <c>Auth0HostSync</c>
/// stays off (<see cref="SiteIntegrationFlagsFactory"/>), as in the test environment until the Management credentials exist: nothing here calls Auth0.
/// </summary>
public class OrgDomainOwnerLoginIntegrationTests(CasazenWebApplicationFactory flagsOff, SiteIntegrationFlagsFactory flagsOn)
    : IClassFixture<CasazenWebApplicationFactory>, IClassFixture<SiteIntegrationFlagsFactory>
{
    [Fact]
    public async Task Retry_OtherOrgId_Forbidden()
    {
        var victimId = $"auth0|owner-login-victim-{Guid.NewGuid():N}";
        var attackerId = $"auth0|owner-login-attacker-{Guid.NewGuid():N}";
        var victim = await flagsOn.SeedOrgForOwnerAsync(victimId);
        var attacker = await flagsOn.SeedOrgForOwnerAsync(attackerId);
        await SetPlanAsync(flagsOn, victim.Id, PlanTier.Pro);
        await SetPlanAsync(flagsOn, attacker.Id, PlanTier.Pro);
        await SetVerifiedDomainAsync(flagsOn, victim.Id, $"victim-{Guid.NewGuid():N}.example.it");
        await UpdateOrgAsync(flagsOn, victim.Id, o =>
        {
            o.OwnerPortalOnOwnDomain = true;
            o.OwnerLoginHostState = OwnerLoginHostState.Failed;
        });

        using var client = flagsOn.CreateAuthenticatedClient(attackerId, "PropertyOwner");
        var response = await client.PostAsync($"/api/orgs/{victim.Id}/domain/owner-login", content: null);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(OwnerLoginHostState.Failed, (await ReadOrgAsync(flagsOn, victim.Id)).OwnerLoginHostState);
    }

    [Fact]
    public async Task Retry_FlagOff_NotFound()
    {
        var ownerId = $"auth0|owner-login-off-{Guid.NewGuid():N}";
        var org = await flagsOff.SeedOrgForOwnerAsync(ownerId);
        await SetPlanAsync(flagsOff, org.Id, PlanTier.Pro);
        await SetVerifiedDomainAsync(flagsOff, org.Id, $"off-{Guid.NewGuid():N}.example.it");

        using var client = flagsOff.CreateAuthenticatedClient(ownerId, "PropertyOwner");
        var response = await client.PostAsync($"/api/orgs/{org.Id}/domain/owner-login", content: null);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(OwnerLoginHostState.NotRequested, (await ReadOrgAsync(flagsOff, org.Id)).OwnerLoginHostState);
    }

    [Fact]
    public async Task Retry_WithoutPlan_PlanRequired()
    {
        var ownerId = $"auth0|owner-login-starter-{Guid.NewGuid():N}";
        var org = await flagsOn.SeedOrgForOwnerAsync(ownerId);
        await SetPlanAsync(flagsOn, org.Id, PlanTier.Starter);
        await UpdateOrgAsync(flagsOn, org.Id, o => o.OwnerLoginHostState = OwnerLoginHostState.Failed);

        using var client = flagsOn.CreateAuthenticatedClient(ownerId, "PropertyOwner");
        var response = await client.PostAsync($"/api/orgs/{org.Id}/domain/owner-login", content: null);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("plan_required", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
        Assert.Equal(OwnerLoginHostState.Failed, (await ReadOrgAsync(flagsOn, org.Id)).OwnerLoginHostState);
    }

    [Fact]
    public async Task Retry_DomainNotVerified_Conflict()
    {
        var ownerId = $"auth0|owner-login-pending-{Guid.NewGuid():N}";
        var org = await flagsOn.SeedOrgForOwnerAsync(ownerId);
        await SetPlanAsync(flagsOn, org.Id, PlanTier.Pro);
        await SetVerifiedDomainAsync(flagsOn, org.Id, $"pending-{Guid.NewGuid():N}.example.it");
        await UpdateOrgAsync(flagsOn, org.Id, o => o.DomainVerificationStatus = DomainVerificationStatus.Pending);

        using var client = flagsOn.CreateAuthenticatedClient(ownerId, "PropertyOwner");
        var response = await client.PostAsync($"/api/orgs/{org.Id}/domain/owner-login", content: null);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("org_domain_not_verified", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
    }

    [Fact]
    public async Task Retry_Auth0HostSyncOff_SendsTheRequestBackToCasaZen()
    {
        var ownerId = $"auth0|owner-login-retry-{Guid.NewGuid():N}";
        var org = await flagsOn.SeedOrgForOwnerAsync(ownerId);
        await SetPlanAsync(flagsOn, org.Id, PlanTier.Pro);
        await SetVerifiedDomainAsync(flagsOn, org.Id, $"retry-{Guid.NewGuid():N}.example.it");
        await UpdateOrgAsync(flagsOn, org.Id, o =>
        {
            o.OwnerPortalOnOwnDomain = true;
            o.OwnerLoginHostState = OwnerLoginHostState.Failed;
        });

        using var client = flagsOn.CreateAuthenticatedClient(ownerId, "PropertyOwner");
        var response = await client.PostAsync($"/api/orgs/{org.Id}/domain/owner-login", content: null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var surfaces = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("surfaces");
        Assert.Equal("Pending", surfaces.GetProperty("ownerLoginHostState").GetString());
        Assert.False(surfaces.GetProperty("ownerLoginAutomatic").GetBoolean());
        Assert.Equal(OwnerLoginHostState.Pending, (await ReadOrgAsync(flagsOn, org.Id)).OwnerLoginHostState);
    }

    [Fact]
    public async Task AdminState_AnAgencyOwnerIsForbidden_ThePlatformAdministratorTakesTheOrgOutOfTheQueue()
    {
        var ownerId = $"auth0|owner-login-queue-{Guid.NewGuid():N}";
        var domain = $"queue-{Guid.NewGuid():N}.example.it";
        var org = await flagsOn.SeedOrgForOwnerAsync(ownerId);
        await SetPlanAsync(flagsOn, org.Id, PlanTier.Pro);
        await SetVerifiedDomainAsync(flagsOn, org.Id, domain);
        await UpdateOrgAsync(flagsOn, org.Id, o =>
        {
            o.OwnerPortalOnOwnDomain = true;
            o.OwnerLoginHostState = OwnerLoginHostState.Pending;
        });
        var body = new { state = "Ready" };

        // The owner of the agency cannot enable its own sign-in: the tenant is CasaZen's.
        using var agency = flagsOn.CreateAuthenticatedClient(ownerId, "PropertyOwner");
        var refused = await agency.PutAsJsonAsync($"/api/admin/orgs/{org.Id}/owner-login-state", body);
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Equal(OwnerLoginHostState.Pending, (await ReadOrgAsync(flagsOn, org.Id)).OwnerLoginHostState);

        using var admin = flagsOn.CreateAuthenticatedClient($"auth0|owner-login-admin-{Guid.NewGuid():N}", "Admin");
        var queue = await admin.GetFromJsonAsync<JsonElement>("/api/admin/owner-login-hosts?state=Pending");
        var item = Assert.Single(queue.EnumerateArray(), i => i.GetProperty("orgId").GetGuid() == org.Id);
        Assert.Equal(domain, item.GetProperty("domain").GetString());
        Assert.Equal("Pending", item.GetProperty("state").GetString());
        Assert.True(item.GetProperty("domainVerified").GetBoolean());

        var set = await admin.PutAsJsonAsync($"/api/admin/orgs/{org.Id}/owner-login-state", body);

        Assert.Equal(HttpStatusCode.OK, set.StatusCode);
        Assert.Equal("Ready", (await set.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("state").GetString());
        var after = await admin.GetFromJsonAsync<JsonElement>("/api/admin/owner-login-hosts?state=Pending");
        Assert.DoesNotContain(after.EnumerateArray(), i => i.GetProperty("orgId").GetGuid() == org.Id);
        using var publicClient = flagsOn.CreateClient();
        var host = await publicClient.GetFromJsonAsync<JsonElement>($"/api/public/resolve-host?host={domain}");
        Assert.True(host.GetProperty("ownerLoginReady").GetBoolean());
    }
}
