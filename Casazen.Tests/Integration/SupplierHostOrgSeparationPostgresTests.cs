using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Infrastructure.Data;
using Casazen.Tests.Integration.Postgres;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Casazen.Tests.Integration;

/// <summary>
/// PL-05 (A1-40) tenant isolation on the real pipeline and PostgreSQL: a supplier org is never the tenant of host data.
/// A supplier who then becomes a host gets its own new Host org; the host data lands there, the supplier data stays on
/// the supplier org, each context sees only its own rows and neither the supplier org nor another host sees the rest.
/// </summary>
public class SupplierHostOrgSeparationPostgresTests(CasazenWebApplicationFactory factory)
    : IClassFixture<CasazenWebApplicationFactory>
{
    private const string ConsentVersion = "2026-06-v1";

    [PostgresFact]
    public async Task SupplierThenHost_HostDataLandsOnANewHostOrgAndEachContextSeesOnlyItsOwnRows()
    {
        var sub = $"auth0|pl05-{Guid.NewGuid():N}";
        var email = $"pl05.{Guid.NewGuid():N}@example.com";
        var otherOwner = $"auth0|pl05-other-{Guid.NewGuid():N}";
        var otherProperty = await factory.SeedPropertyAsync(otherOwner);
        var supplierOrgId = await SeedSupplierAsync(sub, email, legacyOrgIdLink: false);
        var requestId = await SeedRequestAsync(otherProperty, supplierOrgId);

        using var client = factory.CreateAuthenticatedClient(sub, roles: "Supplier", email: email);

        // Supplier only: no host tenant, the host area is closed and shows nothing of the supplier org.
        await AssertOnboardingRequiredAsync(await client.GetAsync("/api/properties"));

        var onboarding = await client.PostAsJsonAsync("/api/users/onboarding", OnboardingPayload());
        Assert.Equal(HttpStatusCode.OK, onboarding.StatusCode);
        var hostOrgId = (await onboarding.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("orgId").GetGuid();
        Assert.NotEqual(supplierOrgId, hostOrgId);

        var created = await client.PostAsJsonAsync("/api/properties", PropertyBody());
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var propertyId = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            // The core A1-40 guarantee: the property is in the host org, a Host org, never in the supplier org.
            var property = await db.Properties.IgnoreQueryFilters().AsNoTracking().SingleAsync(p => p.Id == propertyId);
            Assert.Equal(hostOrgId, property.OrgId);
            Assert.Equal(OrgType.Host, (await db.Orgs.AsNoTracking().SingleAsync(o => o.Id == hostOrgId)).OrgType);
            Assert.False(await db.Properties.IgnoreQueryFilters().AnyAsync(p => p.OrgId == supplierOrgId));
            var user = await db.Users.AsNoTracking().SingleAsync(u => u.Id == sub);
            Assert.Equal(hostOrgId, user.OrgId);
            Assert.Equal(supplierOrgId, user.SupplierOrgId);
        }

        // Host context: its own property only, never another host's.
        var hostList = await client.GetAsync("/api/properties");
        Assert.Equal(HttpStatusCode.OK, hostList.StatusCode);
        var hostBody = await hostList.Content.ReadAsStringAsync();
        Assert.Contains(propertyId.ToString(), hostBody);
        Assert.DoesNotContain(otherProperty.Id.ToString(), hostBody);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/properties/{otherProperty.Id}")).StatusCode);

        // Supplier context of the same account: still its supplier org and its request.
        var inbox = await client.GetAsync("/api/service-requests?view=supplier");
        Assert.Equal(HttpStatusCode.OK, inbox.StatusCode);
        Assert.Contains(requestId.ToString(), await inbox.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/supplier/profile")).StatusCode);

        // The other host does not see the new host's property.
        using var other = factory.CreateAuthenticatedClient(otherOwner, roles: "PropertyOwner");
        Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync($"/api/properties/{propertyId}")).StatusCode);
    }

    [PostgresFact]
    public async Task LegacySupplierOrgInOrgId_IsNeverTheTenantAndOnboardingProvisionsANewHostOrg()
    {
        // Pre-PL-05 shape: OrgId = SupplierOrgId = the supplier org.
        var sub = $"auth0|pl05-legacy-{Guid.NewGuid():N}";
        var email = $"pl05.legacy.{Guid.NewGuid():N}@example.com";
        var supplierOrgId = await SeedSupplierAsync(sub, email, legacyOrgIdLink: true);

        using var client = factory.CreateAuthenticatedClient(sub, roles: "Supplier,PropertyOwner", email: email);
        await AssertOnboardingRequiredAsync(await client.GetAsync("/api/properties"));
        await AssertOnboardingRequiredAsync(await client.GetAsync("/api/orgs/me/settings"));

        var onboarding = await client.PostAsJsonAsync("/api/users/onboarding", OnboardingPayload());

        Assert.Equal(HttpStatusCode.OK, onboarding.StatusCode);
        var hostOrgId = (await onboarding.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("orgId").GetGuid();
        Assert.NotEqual(supplierOrgId, hostOrgId);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/properties")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/supplier/profile")).StatusCode);
    }

    [PostgresFact]
    public async Task RegisterDevice_SupplierOnlyAccount_RegistersUnderItsSupplierOrg()
    {
        var sub = $"auth0|pl05-device-{Guid.NewGuid():N}";
        var email = $"pl05.device.{Guid.NewGuid():N}@example.com";
        var supplierOrgId = await SeedSupplierAsync(sub, email, legacyOrgIdLink: false);
        using var client = factory.CreateAuthenticatedClient(sub, roles: "Supplier", email: email);

        var response = await client.PostAsJsonAsync("/api/devices", new
        {
            platform = "ios",
            pushToken = $"ExponentPushToken[{Guid.NewGuid():N}]",
            deviceId = Guid.NewGuid().ToString("N"),
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(supplierOrgId, (await db.DeviceRegistrations.AsNoTracking().SingleAsync(d => d.UserId == sub)).OrgId);
    }

    private async Task<Guid> SeedSupplierAsync(string sub, string email, bool legacyOrgIdLink)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var org = new OrgEntity
        {
            Name = "Fornitore PL-05",
            Slug = $"supplier-{Guid.NewGuid():N}"[..30],
            DisplayName = "Fornitore PL-05",
            ContactEmail = email,
            OrgType = OrgType.Supplier,
            PlanTier = PlanTier.Starter,
            IsActive = true,
        };
        db.Orgs.Add(org);
        db.SupplierProfiles.Add(new SupplierProfile
        {
            OrgId = org.Id,
            Email = email,
            LegalName = "Fornitore PL-05 Srl",
            Phone = "+39 06 050505",
            Status = SupplierStatus.Active,
            TosAcceptedAt = new DateTime(2026, 9, 1, 8, 0, 0, DateTimeKind.Utc),
            ComuniJson = """["H501"]""",
            CategoriesJson = """["cleaning"]""",
        });
        db.Users.Add(new User
        {
            Id = sub,
            Email = email,
            FirstName = "Mario",
            LastName = "Fornitore",
            Role = UserRole.Supplier,
            OrgId = legacyOrgIdLink ? org.Id : null,
            SupplierOrgId = org.Id,
            IsActive = true,
        });
        await db.SaveChangesAsync();
        return org.Id;
    }

    private async Task<Guid> SeedRequestAsync(Property property, Guid supplierOrgId)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var request = new ServiceRequest
        {
            OrgId = property.OrgId,
            PropertyId = property.Id,
            SupplierOrgId = supplierOrgId,
            Category = "cleaning",
            RentalContext = ServiceRequestRentalContext.LongRent,
        };
        db.ServiceRequests.Add(request);
        await db.SaveChangesAsync();
        return request.Id;
    }

    private static async Task AssertOnboardingRequiredAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.Forbidden, $"{(int)response.StatusCode} {body}");
        using var doc = JsonDocument.Parse(body);
        Assert.Equal("onboarding_required", doc.RootElement.GetProperty("code").GetString());
    }

    private static object OnboardingPayload() => new
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
        },
    };

    private static object PropertyBody() => new
    {
        name = "Casa del fornitore diventato host",
        address = $"Via Separazione {Guid.NewGuid():N}",
        city = "Roma",
        bedrooms = 2,
        bathrooms = 1,
        maxGuests = 4,
        nightlyRate = 90m,
    };
}
