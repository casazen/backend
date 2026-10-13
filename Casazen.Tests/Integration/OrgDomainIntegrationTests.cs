using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
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
    public async Task SetCustomDomain_StoredProWithoutSubscription_Returns403()
    {
        // A3-07 / A3-37: the custom-domain gate follows the effective tier, and a Pro tier nobody pays for is Starter.
        var ownerId = $"auth0|unpaid-pro-{Guid.NewGuid():N}";
        var org = await _factory.SeedOrgForOwnerAsync(ownerId);
        await SetPlanTierAsync(org.Id, PlanTier.Pro, withActiveSubscription: false);

        using var client = _factory.CreateAuthenticatedClient(ownerId, "PropertyOwner");
        var response = await client.PostAsJsonAsync($"/api/orgs/{org.Id}/domain", new
        {
            hostMode = PublicHostMode.CustomDomain,
            customDomain = "www.unpaid-pro-host.it",
        });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
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
    public async Task SetCustomDomain_WhenAlreadyVerifiedSameDomain_PreservesVerification()
    {
        var ownerId = $"auth0|verified-resave-{Guid.NewGuid():N}";
        var org = await _factory.SeedOrgForOwnerAsync(ownerId);
        await SetPlanTierAsync(org.Id, PlanTier.Pro);
        await SetVerifiedCustomDomainAsync(org.Id, "www.verified-resave.it");

        using var client = _factory.CreateAuthenticatedClient(ownerId, "PropertyOwner");
        var response = await client.PostAsJsonAsync($"/api/orgs/{org.Id}/domain", new
        {
            hostMode = PublicHostMode.CustomDomain,
            customDomain = "https://www.verified-resave.it/",
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var updated = await db.Orgs.FindAsync(org.Id);
            Assert.Equal(DomainVerificationStatus.Verified, updated!.DomainVerificationStatus);
            Assert.Equal("verify-token", updated.DomainVerificationToken);
            Assert.Equal("www.verified-resave.it", updated.CustomDomain);
        }

        var resolveResponse = await _factory.CreateClient()
            .GetAsync("/api/public/resolve-host?host=www.verified-resave.it");

        Assert.Equal(HttpStatusCode.OK, resolveResponse.StatusCode);
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
    public async Task SetDomain_AsStaffRole_Returns403_AndDoesNotMutateDomain()
    {
        var userId = $"auth0|staff-{Guid.NewGuid():N}";
        var org = await _factory.SeedOrgForOwnerAsync(userId);

        using var client = _factory.CreateAuthenticatedClient(userId, "Staff");
        var response = await client.PostAsJsonAsync($"/api/orgs/{org.Id}/domain", new
        {
            hostMode = PublicHostMode.CasazenSubdomain,
            subdomain = "staff-hijack",
        });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var persisted = await db.Orgs.FindAsync(org.Id);
        Assert.Equal(PublicHostMode.CasazenPath, persisted!.PublicHostMode);
        Assert.Null(persisted.Subdomain);
    }

    [Fact]
    public async Task SetSubdomain_ConflictingWithAnotherActiveOrgSlug_Returns409()
    {
        var ownerId = $"auth0|owner-{Guid.NewGuid():N}";
        var attackerId = $"auth0|attacker-{Guid.NewGuid():N}";
        var victim = await _factory.SeedOrgForOwnerAsync(ownerId);
        var attacker = await _factory.SeedOrgForOwnerAsync(attackerId);
        var victimSlug = $"villa-mare-{Guid.NewGuid():N}"[..30];
        await SetSlugAsync(victim.Id, victimSlug);

        using var client = _factory.CreateAuthenticatedClient(attackerId, "PropertyOwner");
        var response = await client.PostAsJsonAsync($"/api/orgs/{attacker.Id}/domain", new
        {
            hostMode = PublicHostMode.CasazenSubdomain,
            subdomain = victimSlug,
        });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("org_domain_in_use", problem.GetProperty("code").GetString());
        Assert.Equal("Dominio o sottodominio già in uso.", problem.GetProperty("detail").GetString());
        Assert.False(problem.TryGetProperty("error", out _));
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
        Assert.Equal("Pro", json.GetProperty("planTier").GetString());
        Assert.False(json.GetProperty("branding").GetProperty("showPoweredBy").GetBoolean());
    }

    [Fact]
    public async Task ResolveHost_SubdomainOfStoredProWithoutSubscription_UsesEffectiveStarterTier()
    {
        // A3-37: resolve-host branding and tier use the effective tier, not the stored one.
        var ownerId = $"auth0|resolve-unpaid-{Guid.NewGuid():N}";
        var org = await _factory.SeedOrgForOwnerAsync(ownerId);
        await SetPlanTierAsync(org.Id, PlanTier.Pro, withActiveSubscription: false);
        var slug = $"unpaid-pro-{Guid.NewGuid():N}"[..30];
        await SetSlugAsync(org.Id, slug);
        await SetSubdomainModeAsync(org.Id, slug);

        using var client = _factory.CreateClient();
        var response = await client.GetAsync($"/api/public/resolve-host?host={slug}.casazen.it");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        Assert.Equal(org.Id, json.GetProperty("orgId").GetGuid());
        Assert.Equal("Starter", json.GetProperty("planTier").GetString());
        Assert.True(json.GetProperty("branding").GetProperty("showPoweredBy").GetBoolean());
    }

    [Fact]
    public async Task SwitchingAwayFromSubdomain_ReleasesLabelForAnotherOrg()
    {
        var ownerA = $"auth0|domain-a-{Guid.NewGuid():N}";
        var ownerB = $"auth0|domain-b-{Guid.NewGuid():N}";
        var orgA = await _factory.SeedOrgForOwnerAsync(ownerA);
        var orgB = await _factory.SeedOrgForOwnerAsync(ownerB);
        await SetPlanTierAsync(orgA.Id, PlanTier.Pro);
        var label = $"shared-{Guid.NewGuid():N}"[..20];

        using var clientA = _factory.CreateAuthenticatedClient(ownerA, "PropertyOwner");
        var setSubdomain = await clientA.PostAsJsonAsync($"/api/orgs/{orgA.Id}/domain", new
        {
            hostMode = PublicHostMode.CasazenSubdomain,
            subdomain = label,
        });
        Assert.Equal(HttpStatusCode.OK, setSubdomain.StatusCode);

        var switchToCustom = await clientA.PostAsJsonAsync($"/api/orgs/{orgA.Id}/domain", new
        {
            hostMode = PublicHostMode.CustomDomain,
            customDomain = $"www.{label}.it",
        });
        Assert.Equal(HttpStatusCode.OK, switchToCustom.StatusCode);

        using var clientB = _factory.CreateAuthenticatedClient(ownerB, "PropertyOwner");
        var reuseSubdomain = await clientB.PostAsJsonAsync($"/api/orgs/{orgB.Id}/domain", new
        {
            hostMode = PublicHostMode.CasazenSubdomain,
            subdomain = label,
        });
        Assert.Equal(HttpStatusCode.OK, reuseSubdomain.StatusCode);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var reloadedOrgA = await db.Orgs.FindAsync(orgA.Id);
        Assert.Null(reloadedOrgA!.Subdomain);

        using var publicClient = _factory.CreateClient();
        var resolve = await publicClient.GetAsync($"/api/public/resolve-host?host={label}.casazen.it");
        Assert.Equal(HttpStatusCode.OK, resolve.StatusCode);
        var resolvedJson = await resolve.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        Assert.Equal(orgB.Id, resolvedJson.GetProperty("orgId").GetGuid());
    }

    [Fact]
    public async Task ResolveHost_SlugAsSubdomainOfAnOrgOnThePathMode_Returns404()
    {
        // BK-16 (A3-08): no wildcard on the base domain, only the orgs that chose the subdomain mode are served on a label.
        var org = await _factory.SeedOrgForOwnerAsync($"auth0|resolve-path-{Guid.NewGuid():N}");
        var slug = $"path-mode-{Guid.NewGuid():N}"[..28];
        await SetSlugAsync(org.Id, slug);

        var response = await _factory.CreateClient().GetAsync($"/api/public/resolve-host?host={slug}.casazen.it");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task VerifyDomain_OnStarterWithALeftoverCustomDomain_Returns403NotAVerifiedDomainNobodyCanOpen()
    {
        // A3-07 / BK-16: the domain is a Pro feature; after a downgrade a verification would say "verified" for a site that is not served.
        var ownerId = $"auth0|verify-starter-{Guid.NewGuid():N}";
        var org = await _factory.SeedOrgForOwnerAsync(ownerId);
        await SetPlanTierAsync(org.Id, PlanTier.Pro);
        using var client = _factory.CreateAuthenticatedClient(ownerId, "PropertyOwner");
        var set = await client.PostAsJsonAsync($"/api/orgs/{org.Id}/domain", new
        {
            hostMode = PublicHostMode.CustomDomain,
            customDomain = "www.downgraded-host.it",
        });
        Assert.Equal(HttpStatusCode.OK, set.StatusCode);
        await SetPlanTierAsync(org.Id, PlanTier.Starter);

        var verify = await client.PostAsJsonAsync($"/api/orgs/{org.Id}/domain/verify", new { });

        Assert.Equal(HttpStatusCode.Forbidden, verify.StatusCode);
        Assert.Equal("plan_required", (await verify.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>()).GetProperty("code").GetString());
    }

    /// <summary>
    /// Pro/Scale take effect only while a subscription pays for them (#274): a paid tier is seeded with an active
    /// Stripe subscription unless <paramref name="withActiveSubscription"/> is false.
    /// </summary>
    private async Task SetPlanTierAsync(Guid orgId, PlanTier tier, bool withActiveSubscription = true)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var org = await db.Orgs.FindAsync(orgId);
        var paid = tier != PlanTier.Starter && withActiveSubscription;
        org!.PlanTier = tier;
        org.SubscriptionId = paid ? $"sub_test_{orgId:N}" : null;
        org.SubscriptionStatus = paid ? SubscriptionStatus.Active : SubscriptionStatus.None;
        await db.SaveChangesAsync();
    }

    private async Task SetSlugAsync(Guid orgId, string slug)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var org = await db.Orgs.FindAsync(orgId);
        org!.Slug = slug;
        org.Subdomain = null;
        org.PublicHostMode = PublicHostMode.CasazenPath;
        await db.SaveChangesAsync();
    }

    /// <summary>The org chose the subdomain mode with <paramref name="label"/>: the only way a label is served (BK-16).</summary>
    private async Task SetSubdomainModeAsync(Guid orgId, string label)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var org = await db.Orgs.FindAsync(orgId);
        org!.PublicHostMode = PublicHostMode.CasazenSubdomain;
        org.Subdomain = label;
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
