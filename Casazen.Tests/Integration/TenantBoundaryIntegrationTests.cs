using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Utilities;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Casazen.Tests.Integration;

/// <summary>
/// US-004 (#202) tenant boundary over HTTP. Exercises the real request pipeline (auth + EF global
/// tenant filter + entitlement) on the in-memory harness:
/// AC7 cross-org reads return 404/empty, AC8 plan entitlement, AC9 /me returns org, AC10 an owner
/// still sees exactly their own rows post-tenant-scoping. Each test uses a unique owner so the
/// shared class-fixture in-memory store cannot leak property counts between tests.
/// </summary>
public class TenantBoundaryIntegrationTests : IClassFixture<CasazenWebApplicationFactory>
{
    private readonly CasazenWebApplicationFactory _factory;
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    public TenantBoundaryIntegrationTests(CasazenWebApplicationFactory factory) => _factory = factory;

    private static string NewOwner() => $"auth0|owner-{Guid.NewGuid():N}";

    private static object ValidPropertyBody() => new
    {
        name = "Nuovo Appartamento",
        address = "Via Roma 1",
        city = "Rome",
        bedrooms = 2,
        bathrooms = 1,
        maxGuests = 4,
        nightlyRate = 90m,
        cinCode = "IT058091C27G5FFZDZ",
    };

    [Fact]
    public async Task AC9_GetMe_ReturnsCallerOrg()
    {
        var owner = NewOwner();
        await _factory.SeedPropertyAsync(ownerId: owner);
        var client = _factory.CreateAuthenticatedClient(userId: owner);

        var response = await client.GetAsync("/api/users/me");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = doc.RootElement;
        Assert.NotEqual(JsonValueKind.Null, root.GetProperty("orgId").ValueKind);
        var org = root.GetProperty("org");
        Assert.NotEqual(JsonValueKind.Null, org.ValueKind);
        Assert.False(string.IsNullOrWhiteSpace(org.GetProperty("slug").GetString()));
        Assert.Equal("Starter", org.GetProperty("planTier").GetString());
    }

    [Fact]
    public async Task AC8_GetEntitlement_ReturnsTierLimitsAndUsage()
    {
        var owner = NewOwner();
        await _factory.SeedPropertyAsync(ownerId: owner);
        var client = _factory.CreateAuthenticatedClient(userId: owner, roles: "PropertyOwner");

        var response = await client.GetAsync("/api/orgs/me/entitlement");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = doc.RootElement;
        Assert.Equal("Starter", root.GetProperty("planTier").GetString());
        Assert.Equal(3, root.GetProperty("limits").GetProperty("maxProperties").GetInt32());
        Assert.Equal(1, root.GetProperty("usage").GetProperty("properties").GetInt32());
        Assert.True(root.GetProperty("canAddProperty").GetBoolean());
    }

    [Fact]
    public async Task AC8_CreateProperty_OverStarterLimit_Returns403PlanLimitReached()
    {
        var owner = NewOwner();
        // Starter limit is 3 — seed the org to exactly the limit.
        await _factory.SeedPropertyAsync(ownerId: owner);
        await _factory.SeedPropertyAsync(ownerId: owner);
        await _factory.SeedPropertyAsync(ownerId: owner);
        var client = _factory.CreateAuthenticatedClient(userId: owner, roles: "PropertyOwner");

        var response = await client.PostAsJsonAsync("/api/properties", ValidPropertyBody());

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("plan_limit_reached", doc.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task AC7_CrossOrg_GetPropertyById_Returns404_AndListExcludesIt()
    {
        var ownerA = NewOwner();
        var ownerB = NewOwner();
        var propertyA = await _factory.SeedPropertyAsync(ownerId: ownerA);
        await _factory.SeedOrgForOwnerAsync(ownerId: ownerB); // B has its own org, no properties

        var clientB = _factory.CreateAuthenticatedClient(userId: ownerB, roles: "PropertyOwner");

        // Direct cross-org read is filtered out at the data layer → 404 (never another org's row).
        var byId = await clientB.GetAsync($"/api/properties/{propertyA.Id}");
        Assert.Equal(HttpStatusCode.NotFound, byId.StatusCode);

        // B's collection never contains A's property.
        var list = await clientB.GetAsync("/api/properties");
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        using var doc = JsonDocument.Parse(await list.Content.ReadAsStringAsync());
        var ids = doc.RootElement.EnumerateArray().Select(e => e.GetProperty("id").GetGuid());
        Assert.DoesNotContain(propertyA.Id, ids);
    }

    [Fact]
    public async Task AC10_Owner_StillSeesOwnRows_PostTenantScoping()
    {
        var owner = NewOwner();
        var property = await _factory.SeedPropertyAsync(ownerId: owner);
        var client = _factory.CreateAuthenticatedClient(userId: owner, roles: "PropertyOwner");

        var byId = await client.GetAsync($"/api/properties/{property.Id}");
        Assert.Equal(HttpStatusCode.OK, byId.StatusCode);

        var list = await client.GetAsync("/api/properties");
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        using var doc = JsonDocument.Parse(await list.Content.ReadAsStringAsync());
        var ids = doc.RootElement.EnumerateArray().Select(e => e.GetProperty("id").GetGuid());
        Assert.Contains(property.Id, ids);
    }

    [Fact]
    public async Task CreateBooking_WithExistingGuestEmail_DoesNotGrantAccessToOtherOrgGuest()
    {
        var ownerA = NewOwner();
        var ownerB = NewOwner();
        var propertyA = await _factory.SeedPropertyAsync(ownerId: ownerA);
        var propertyB = await _factory.SeedPropertyAsync(ownerId: ownerB);

        var existingGuest = new Guest
        {
            OrgId = propertyA.OrgId,
            FirstName = "Alice",
            LastName = "Private",
            Email = "shared-guest@example.com",
            PhoneNumber = "+391111111111",
            Country = "France",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.Guests.Add(existingGuest);
            db.Bookings.Add(new Booking
            {
                PropertyId = propertyA.Id,
                OrgId = propertyA.OrgId,
                GuestId = existingGuest.Id,
                CheckInDate = TimeProvider.System.TodayInRome().AddDays(1),
                CheckOutDate = TimeProvider.System.TodayInRome().AddDays(3),
                NumberOfGuests = 2,
                Status = BookingStatus.Confirmed,
                Source = BookingSource.Direct,
                BasePrice = 200m,
                TotalPrice = 200m,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        var clientB = _factory.CreateAuthenticatedClient(userId: ownerB, roles: "PropertyOwner");
        var create = await clientB.PostAsJsonAsync("/api/bookings", new
        {
            propertyId = propertyB.Id,
            checkInDate = TimeProvider.System.TodayInRome().AddDays(10),
            checkOutDate = TimeProvider.System.TodayInRome().AddDays(12),
            numberOfGuests = 2,
            guest = new
            {
                firstName = "Mario",
                lastName = "Rossi",
                email = existingGuest.Email,
                phone = "+393331234567",
                country = "Italia",
            },
        });

        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        using (var createdDoc = JsonDocument.Parse(await create.Content.ReadAsStringAsync()))
        {
            var createdGuest = createdDoc.RootElement.GetProperty("guest");
            Assert.Equal("Mario", createdGuest.GetProperty("firstName").GetString());
            Assert.Equal("+393331234567", createdGuest.GetProperty("phone").GetString());
        }

        var leakedGuest = await clientB.GetAsync($"/api/guests/{existingGuest.Id}");
        Assert.Equal(HttpStatusCode.NotFound, leakedGuest.StatusCode);
    }

    [Fact]
    public async Task Onboarding_WithProPlan_ProvisionsStarterOrgWithoutSubscription()
    {
        // A1-03 / A9-02: choosing a paid plan in the wizard must not grant it; paid tiers come from Stripe only.
        var owner = NewOwner();
        var client = _factory.CreateAuthenticatedClient(userId: owner, roles: "PropertyOwner");

        var response = await client.PostAsJsonAsync("/api/users/onboarding", new
        {
            rentalType = "ShortTerm",
            planTier = "Pro",
            consents = new
            {
                tosAccepted = true,
                tosVersion = "2026-06-v1",
                privacyAccepted = true,
                privacyVersion = "2026-06-v1",
                dpaAccepted = true,
                dpaVersion = "2026-06-v1",
                subprocessorsAcknowledged = true,
                subprocessorsVersion = "2026-06-v1",
            },
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var orgId = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("orgId").GetGuid();

        var me = await client.GetAsync("/api/users/me");
        using var doc = JsonDocument.Parse(await me.Content.ReadAsStringAsync());
        Assert.Equal("Starter", doc.RootElement.GetProperty("org").GetProperty("planTier").GetString());

        var stored = await GetOrgAsync(orgId);
        Assert.Equal(PlanTier.Starter, stored.PlanTier);
        Assert.Equal(SubscriptionStatus.None, stored.SubscriptionStatus);
    }

    [Fact]
    public async Task Onboarding_WithUnknownPlan_Returns400()
    {
        var owner = NewOwner();
        var client = _factory.CreateAuthenticatedClient(userId: owner, roles: "PropertyOwner");

        var response = await client.PutAsJsonAsync("/api/users/onboarding", new { rentalType = "ShortTerm", planTier = "Gold" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task GetMe_StoredProWithoutSubscription_ReturnsEffectiveStarterTier()
    {
        var owner = NewOwner();
        var property = await _factory.SeedPropertyAsync(ownerId: owner);
        await SetOrgPlanAsync(property.OrgId, PlanTier.Pro, SubscriptionStatus.None, subscriptionId: null);
        var client = _factory.CreateAuthenticatedClient(userId: owner, roles: "PropertyOwner");

        var me = await client.GetAsync("/api/users/me");

        Assert.Equal(HttpStatusCode.OK, me.StatusCode);
        using var doc = JsonDocument.Parse(await me.Content.ReadAsStringAsync());
        Assert.Equal("Starter", doc.RootElement.GetProperty("org").GetProperty("planTier").GetString());
    }

    [Fact]
    public async Task UpdateMyPlan_UpgradeWithoutSubscription_Returns403SubscriptionRequired()
    {
        // #274: the self-serve PUT used to hand out Pro/Scale for free (A1-03, A3-07, A9-02).
        var owner = NewOwner();
        var property = await _factory.SeedPropertyAsync(ownerId: owner);
        var client = _factory.CreateAuthenticatedClient(userId: owner, roles: "PropertyOwner");

        var response = await client.PutAsJsonAsync("/api/orgs/me/plan", new { planTier = "Scale" });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("subscription_required", doc.RootElement.GetProperty("code").GetString());
        Assert.False(string.IsNullOrWhiteSpace(doc.RootElement.GetProperty("detail").GetString()));

        var stored = await GetOrgAsync(property.OrgId);
        Assert.Equal(PlanTier.Starter, stored.PlanTier);

        var entitlement = await client.GetAsync("/api/orgs/me/entitlement");
        using var entitlementDoc = JsonDocument.Parse(await entitlement.Content.ReadAsStringAsync());
        Assert.Equal("Starter", entitlementDoc.RootElement.GetProperty("planTier").GetString());
        Assert.False(entitlementDoc.RootElement.GetProperty("canUseCustomDomain").GetBoolean());
    }

    [Fact]
    public async Task UpdateMyPlan_UpgradeAfterCanceledSubscription_Returns403SubscriptionRequired()
    {
        var owner = NewOwner();
        var property = await _factory.SeedPropertyAsync(ownerId: owner);
        await SetOrgPlanAsync(property.OrgId, PlanTier.Starter, SubscriptionStatus.Canceled, $"sub_{Guid.NewGuid():N}");
        var client = _factory.CreateAuthenticatedClient(userId: owner, roles: "PropertyOwner");

        var response = await client.PutAsJsonAsync("/api/orgs/me/plan", new { planTier = "Pro" });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("subscription_required", doc.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task UpdateMyPlan_BackToStarterWithoutSubscription_Returns200()
    {
        // A Pro tier nobody pays for (e.g. granted by the old free upgrade) can always go back to Starter.
        var owner = NewOwner();
        var property = await _factory.SeedPropertyAsync(ownerId: owner);
        var orgId = property.OrgId;
        await SetOrgPlanAsync(orgId, PlanTier.Scale, SubscriptionStatus.None, subscriptionId: null);
        var client = _factory.CreateAuthenticatedClient(userId: owner, roles: "PropertyOwner");

        var response = await client.PutAsJsonAsync("/api/orgs/me/plan", new { planTier = "Starter" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("Starter", doc.RootElement.GetProperty("planTier").GetString());
        Assert.Equal(PlanTier.Starter, (await GetOrgAsync(orgId)).PlanTier);
    }

    [Fact]
    public async Task UpdateMyPlan_WithActiveSubscription_Returns409ManagedByStripe()
    {
        var owner = NewOwner();
        var property = await _factory.SeedPropertyAsync(ownerId: owner);
        var orgId = property.OrgId;
        await SetOrgPlanAsync(orgId, PlanTier.Pro, SubscriptionStatus.Active, $"sub_{Guid.NewGuid():N}");
        var client = _factory.CreateAuthenticatedClient(userId: owner, roles: "PropertyOwner");

        var response = await client.PutAsJsonAsync("/api/orgs/me/plan", new { planTier = "Starter" });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("managed_by_stripe", doc.RootElement.GetProperty("code").GetString());
        Assert.Equal(PlanTier.Pro, (await GetOrgAsync(orgId)).PlanTier);
    }

    [Fact]
    public async Task UpdateMyPlan_AsStaff_Returns403()
    {
        // Only the org's billing admin may change the plan.
        var staff = NewOwner();
        await _factory.SeedOrgForOwnerAsync(ownerId: staff);
        var client = _factory.CreateAuthenticatedClient(userId: staff, roles: "Staff");

        var response = await client.PutAsJsonAsync("/api/orgs/me/plan", new { planTier = "Starter" });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("forbidden", doc.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task AdminUpdateOrgPlan_WithActiveSubscription_Returns409ManagedByStripe()
    {
        // A1-41: the next Stripe webhook would silently overwrite an admin change.
        var owner = NewOwner();
        var property = await _factory.SeedPropertyAsync(ownerId: owner);
        var orgId = property.OrgId;
        await SetOrgPlanAsync(orgId, PlanTier.Pro, SubscriptionStatus.Active, $"sub_{Guid.NewGuid():N}");
        var adminClient = _factory.CreateAuthenticatedClient(userId: "auth0|admin", roles: "Admin");

        var response = await adminClient.PatchAsJsonAsync($"/api/admin/orgs/{orgId}/plan", new { planTier = "Scale" });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("managed_by_stripe", doc.RootElement.GetProperty("code").GetString());
        Assert.Equal(PlanTier.Pro, (await GetOrgAsync(orgId)).PlanTier);
    }

    [Fact]
    public async Task AdminUpdateOrgPlan_UpgradeWithoutSubscription_Returns409SubscriptionRequired()
    {
        // Without a subscription a paid tier would not take effect: refuse instead of reporting a no-op success.
        var owner = NewOwner();
        var property = await _factory.SeedPropertyAsync(ownerId: owner);
        var orgId = property.OrgId;
        var adminClient = _factory.CreateAuthenticatedClient(userId: "auth0|admin", roles: "Admin");

        var response = await adminClient.PatchAsJsonAsync($"/api/admin/orgs/{orgId}/plan", new { planTier = "Pro" });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("subscription_required", doc.RootElement.GetProperty("code").GetString());
        Assert.Equal(PlanTier.Starter, (await GetOrgAsync(orgId)).PlanTier);
    }

    [Fact]
    public async Task AdminUpdateOrgPlan_DowngradeWithoutSubscription_Returns200()
    {
        var owner = NewOwner();
        var property = await _factory.SeedPropertyAsync(ownerId: owner);
        var orgId = property.OrgId;
        await SetOrgPlanAsync(orgId, PlanTier.Pro, SubscriptionStatus.None, subscriptionId: null);
        var adminClient = _factory.CreateAuthenticatedClient(userId: "auth0|admin", roles: "Admin");

        var response = await adminClient.PatchAsJsonAsync($"/api/admin/orgs/{orgId}/plan", new { planTier = "Starter" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("Starter", doc.RootElement.GetProperty("planTier").GetString());
        Assert.Equal(PlanTier.Starter, (await GetOrgAsync(orgId)).PlanTier);
    }

    private async Task SetOrgPlanAsync(Guid orgId, PlanTier tier, SubscriptionStatus status, string? subscriptionId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var org = await db.Orgs.IgnoreQueryFilters().SingleAsync(o => o.Id == orgId);
        org.PlanTier = tier;
        org.SubscriptionStatus = status;
        org.SubscriptionId = subscriptionId;
        await db.SaveChangesAsync();
    }

    private async Task<OrgEntity> GetOrgAsync(Guid orgId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.Orgs.IgnoreQueryFilters().AsNoTracking().SingleAsync(o => o.Id == orgId);
    }
}
