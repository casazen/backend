using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Casazen.Core.Entities.Enums;
using Casazen.Infrastructure.Data;
using Casazen.Tests.Integration.Postgres;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Casazen.Tests.Integration;

/// <summary>
/// The web host of the BL-01 tests, with <c>Entitlement:OpenAccess</c> set the way a test needs (a value left <c>null</c> is
/// not set at all, like on a deployment that never heard of it). The comuni sample is not loaded and, on the in-memory
/// fallback of a local run, the host gets a store of its own: two hosts starting together on the shared one collide.
/// </summary>
public abstract class OpenAccessFactoryBase(string? enabled, string? tier) : CasazenWebApplicationFactory
{
    protected override bool SeedComuneSample => false;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        var inMemoryStore = $"open-access-{Guid.NewGuid():N}";

        var settings = new Dictionary<string, string?>();
        if (enabled is not null)
            settings["Entitlement:OpenAccess:Enabled"] = enabled;
        if (tier is not null)
            settings["Entitlement:OpenAccess:Tier"] = tier;
        builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(settings));

        builder.ConfigureTestServices(services =>
        {
            if (UsesPostgreSql)
                return;

            RemoveAllOf<DbContextOptions<AppDbContext>>(services);
            RemoveAllOf<IDbContextOptionsConfiguration<AppDbContext>>(services);
            services.AddDbContext<AppDbContext>(options =>
            {
                options.UseInMemoryDatabase(inMemoryStore);
                options.ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning));
            });
        });
    }
}

public sealed class OpenAccessUnsetFactory() : OpenAccessFactoryBase(null, null);

public sealed class OpenAccessOnFactory() : OpenAccessFactoryBase("true", null);

public sealed class OpenAccessInvalidSwitchFactory() : OpenAccessFactoryBase("yes", null);

public sealed class OpenAccessInvalidTierFactory() : OpenAccessFactoryBase("true", "Enterprise");

public class OpenAccessOffIntegrationTests(OpenAccessUnsetFactory factory) : IClassFixture<OpenAccessUnsetFactory>
{
    private static string NewOwner() => $"auth0|bl01-off-{Guid.NewGuid():N}";

    [Fact]
    public async Task GetEntitlement_DefaultConfiguration_IsStarterAndNotOpen()
    {
        var owner = NewOwner();
        await factory.SeedPropertyAsync(ownerId: owner);
        using var client = factory.CreateAuthenticatedClient(userId: owner, roles: "PropertyOwner");

        var response = await client.GetAsync("/api/orgs/me/entitlement");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Starter", body.GetProperty("planTier").GetString());
        Assert.False(body.GetProperty("openAccess").GetBoolean());
        Assert.Equal(3, body.GetProperty("limits").GetProperty("maxProperties").GetInt32());
        Assert.False(body.GetProperty("canUseCustomDomain").GetBoolean());
    }

    [Fact]
    public async Task CreateProperty_DefaultConfiguration_OverTheStarterLimit_StillReturns403PlanLimitReached()
    {
        var owner = NewOwner();
        for (var i = 0; i < 3; i++)
            await factory.SeedPropertyAsync(ownerId: owner);
        using var client = factory.CreateAuthenticatedClient(userId: owner, roles: "PropertyOwner");

        var response = await client.PostAsJsonAsync("/api/properties", OpenAccessHttp.PropertyBody());

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("plan_limit_reached", body.GetProperty("code").GetString());
    }
}

public class OpenAccessOnIntegrationTests(OpenAccessOnFactory factory) : IClassFixture<OpenAccessOnFactory>
{
    private static string NewOwner() => $"auth0|bl01-on-{Guid.NewGuid():N}";

    [Fact]
    public async Task GetEntitlement_OrgWithoutSubscription_IsServedAsScaleWithOpenAccess()
    {
        var owner = NewOwner();
        Guid orgId = default;
        for (var i = 0; i < 4; i++)
            orgId = (await factory.SeedPropertyAsync(ownerId: owner)).OrgId;
        using var client = factory.CreateAuthenticatedClient(userId: owner, roles: "PropertyOwner");

        var response = await client.GetAsync("/api/orgs/me/entitlement");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Scale", body.GetProperty("planTier").GetString());
        Assert.True(body.GetProperty("openAccess").GetBoolean());
        Assert.Equal(int.MaxValue, body.GetProperty("limits").GetProperty("maxProperties").GetInt32());
        Assert.Equal(4, body.GetProperty("usage").GetProperty("properties").GetInt32());
        Assert.True(body.GetProperty("canAddProperty").GetBoolean());
        Assert.True(body.GetProperty("canUseCustomDomain").GetBoolean());

        var stored = await OpenAccessHttp.GetOrgAsync(factory, orgId);
        Assert.Equal(PlanTier.Starter, stored.PlanTier);
        Assert.Equal(SubscriptionStatus.None, stored.SubscriptionStatus);
        Assert.Null(stored.SubscriptionId);
    }

    [Fact]
    public async Task GetSubscription_BillingPageStillShowsTheStoredPlan()
    {
        var owner = NewOwner();
        await factory.SeedPropertyAsync(ownerId: owner);
        using var client = factory.CreateAuthenticatedClient(userId: owner, roles: "PropertyOwner");

        var response = await client.GetAsync("/api/billing/subscription");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Starter", body.GetProperty("planTier").GetString());
        Assert.Equal("none", body.GetProperty("status").GetString());
    }

    [Fact]
    public async Task GetMe_ReportsTheEffectiveTier()
    {
        var owner = NewOwner();
        await factory.SeedPropertyAsync(ownerId: owner);
        using var client = factory.CreateAuthenticatedClient(userId: owner, roles: "PropertyOwner");

        var response = await client.GetAsync("/api/users/me");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Scale", body.GetProperty("org").GetProperty("planTier").GetString());
    }

    [PostgresFact]
    public async Task CreateProperty_BeyondTheStarterLimit_Returns201()
    {
        var owner = NewOwner();
        for (var i = 0; i < 3; i++)
            await factory.SeedPropertyAsync(ownerId: owner);
        using var client = factory.CreateAuthenticatedClient(userId: owner, roles: "PropertyOwner");

        var response = await client.PostAsJsonAsync("/api/properties", OpenAccessHttp.PropertyBody());

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Fact]
    public async Task UpdateMyPlan_UpgradeWithoutSubscription_StillReturns403SubscriptionRequired()
    {
        var owner = NewOwner();
        var property = await factory.SeedPropertyAsync(ownerId: owner);
        using var client = factory.CreateAuthenticatedClient(userId: owner, roles: "PropertyOwner");

        var response = await client.PutAsJsonAsync("/api/orgs/me/plan", new { planTier = "Pro" });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("subscription_required", body.GetProperty("code").GetString());
        Assert.Equal(PlanTier.Starter, (await OpenAccessHttp.GetOrgAsync(factory, property.OrgId)).PlanTier);
    }

    [Fact]
    public async Task UpdateMyPlan_BackToStarterWithoutSubscription_StoresStarterAndTheAccessStaysOpen()
    {
        var owner = NewOwner();
        var property = await factory.SeedPropertyAsync(ownerId: owner);
        await OpenAccessHttp.SetOrgPlanAsync(factory, property.OrgId, PlanTier.Pro, SubscriptionStatus.None, subscriptionId: null);
        using var client = factory.CreateAuthenticatedClient(userId: owner, roles: "PropertyOwner");

        var response = await client.PutAsJsonAsync("/api/orgs/me/plan", new { planTier = "Starter" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Scale", body.GetProperty("planTier").GetString());
        Assert.True(body.GetProperty("openAccess").GetBoolean());
        Assert.Equal(PlanTier.Starter, (await OpenAccessHttp.GetOrgAsync(factory, property.OrgId)).PlanTier);
    }

    [Fact]
    public async Task UpdateMyPlan_WithActiveSubscription_StillReturns409ManagedByStripe()
    {
        var owner = NewOwner();
        var property = await factory.SeedPropertyAsync(ownerId: owner);
        await OpenAccessHttp.SetOrgPlanAsync(factory, property.OrgId, PlanTier.Pro, SubscriptionStatus.Active, $"sub_{Guid.NewGuid():N}");
        using var client = factory.CreateAuthenticatedClient(userId: owner, roles: "PropertyOwner");

        var response = await client.PutAsJsonAsync("/api/orgs/me/plan", new { planTier = "Starter" });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("managed_by_stripe", body.GetProperty("code").GetString());
        Assert.Equal(PlanTier.Pro, (await OpenAccessHttp.GetOrgAsync(factory, property.OrgId)).PlanTier);
    }

    [Fact]
    public async Task AdminUpdateOrgPlan_UpgradeWithoutSubscription_StillReturns409SubscriptionRequired()
    {
        var owner = NewOwner();
        var property = await factory.SeedPropertyAsync(ownerId: owner);
        using var admin = factory.CreateAuthenticatedClient(userId: "auth0|bl01-admin", roles: "Admin");

        var response = await admin.PatchAsJsonAsync($"/api/admin/orgs/{property.OrgId}/plan", new { planTier = "Pro" });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("subscription_required", body.GetProperty("code").GetString());
        Assert.Equal(PlanTier.Starter, (await OpenAccessHttp.GetOrgAsync(factory, property.OrgId)).PlanTier);
    }
}

public class OpenAccessStartupIntegrationTests
{
    [Fact]
    public void Host_SwitchNotABoolean_DoesNotStart()
    {
        using var factory = new OpenAccessInvalidSwitchFactory();

        var ex = Assert.ThrowsAny<Exception>(() => factory.CreateClient());

        Assert.Contains("Entitlement__OpenAccess__Enabled must be true or false", ex.ToString());
    }

    [Fact]
    public void Host_TierNotAPlan_DoesNotStart()
    {
        using var factory = new OpenAccessInvalidTierFactory();

        var ex = Assert.ThrowsAny<Exception>(() => factory.CreateClient());

        Assert.Contains("Entitlement__OpenAccess__Tier must be the name of a plan", ex.ToString());
    }
}

internal static class OpenAccessHttp
{
    private const string CinOfRoma = "IT058091C27G5FFZDZ";

    public static object PropertyBody() => new
    {
        name = "Appartamento accesso aperto",
        address = $"Via di prova {Guid.NewGuid():N}",
        city = "Roma",
        bedrooms = 2,
        bathrooms = 1,
        maxGuests = 4,
        nightlyRate = 90m,
        cinCode = CinOfRoma,
    };

    public static async Task SetOrgPlanAsync(
        CasazenWebApplicationFactory factory, Guid orgId, PlanTier tier, SubscriptionStatus status, string? subscriptionId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var org = await db.Orgs.IgnoreQueryFilters().SingleAsync(o => o.Id == orgId);
        org.PlanTier = tier;
        org.SubscriptionStatus = status;
        org.SubscriptionId = subscriptionId;
        await db.SaveChangesAsync();
    }

    public static async Task<OrgEntity> GetOrgAsync(CasazenWebApplicationFactory factory, Guid orgId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.Orgs.IgnoreQueryFilters().AsNoTracking().SingleAsync(o => o.Id == orgId);
    }
}
