using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
// OrgEntity alias from Casazen.Tests.csproj

namespace Casazen.Tests.Integration;

public class ServiceRequestIntegrationTests : IClassFixture<CasazenWebApplicationFactory>
{
    private readonly CasazenWebApplicationFactory _factory;

    public ServiceRequestIntegrationTests(CasazenWebApplicationFactory factory) => _factory = factory;

    [Fact]
    public async Task Create_AsHost_Returns201()
    {
        var (hostId, _, propertyId, supplierOrgId, _) = await SeedScenarioAsync();
        using var client = _factory.CreateAuthenticatedClient(hostId, "PropertyOwner");

        var response = await client.PostAsJsonAsync("/api/service-requests", new
        {
            propertyId,
            supplierOrgId,
            category = "cleaning",
            notes = "Turnover dopo checkout",
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Richiesto", body.GetProperty("status").GetString());
    }

    [Fact]
    public async Task Create_WithBookingId_Returns400()
    {
        var (hostId, _, propertyId, supplierOrgId, _) = await SeedScenarioAsync();
        using var client = _factory.CreateAuthenticatedClient(hostId, "PropertyOwner");

        var response = await client.PostAsJsonAsync("/api/service-requests", new
        {
            propertyId,
            bookingId = Guid.NewGuid(),
            supplierOrgId,
            category = "cleaning",
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Create_WithChargeToGuest_Returns400()
    {
        var (hostId, _, propertyId, supplierOrgId, _) = await SeedScenarioAsync();
        using var client = _factory.CreateAuthenticatedClient(hostId, "PropertyOwner");

        var response = await client.PostAsJsonAsync("/api/service-requests", new
        {
            propertyId,
            supplierOrgId,
            category = "cleaning",
            chargeToGuest = true,
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task GetInbox_AsSupplier_ReturnsCreatedRequest()
    {
        var (hostId, _, propertyId, supplierOrgId, supplierUserId) = await SeedScenarioAsync();

        using var hostClient = _factory.CreateAuthenticatedClient(hostId, "PropertyOwner");
        await hostClient.PostAsJsonAsync("/api/service-requests", new
        {
            propertyId,
            supplierOrgId,
            category = "cleaning",
        });

        using var supplierClient = _factory.CreateAuthenticatedClient(supplierUserId, "Supplier");
        var inbox = await supplierClient.GetAsync("/api/supplier/inbox");

        Assert.Equal(HttpStatusCode.OK, inbox.StatusCode);
        var body = await inbox.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(body.GetProperty("total").GetInt32() >= 1);
    }

    [Fact]
    public async Task CompleteFlow_TakeCompleteMarkPaid_Succeeds()
    {
        var (hostId, _, propertyId, supplierOrgId, supplierUserId) = await SeedScenarioAsync();

        using var hostClient = _factory.CreateAuthenticatedClient(hostId, "PropertyOwner");
        var create = await hostClient.PostAsJsonAsync("/api/service-requests", new
        {
            propertyId,
            supplierOrgId,
            category = "cleaning",
        });
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        var created = await create.Content.ReadFromJsonAsync<JsonElement>();
        var id = created.GetProperty("id").GetGuid();

        using var supplierClient = _factory.CreateAuthenticatedClient(supplierUserId, "Supplier");
        var take = await supplierClient.PostAsJsonAsync($"/api/service-requests/{id}/take", new { });
        Assert.Equal(HttpStatusCode.OK, take.StatusCode);

        var complete = await supplierClient.PostAsJsonAsync($"/api/service-requests/{id}/complete", new { });
        Assert.Equal(HttpStatusCode.OK, complete.StatusCode);

        var paid = await hostClient.PostAsJsonAsync($"/api/service-requests/{id}/mark-paid", new { });
        Assert.Equal(HttpStatusCode.OK, paid.StatusCode);
        var paidBody = await paid.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Pagato", paidBody.GetProperty("status").GetString());
    }

    [Fact]
    public async Task Create_AsOtherOrgHost_Returns403Or404()
    {
        var (_, _, propertyId, supplierOrgId, _) = await SeedScenarioAsync();
        var otherHost = $"auth0|other-{Guid.NewGuid():N}";
        await _factory.SeedOrgForOwnerAsync(otherHost);

        using var client = _factory.CreateAuthenticatedClient(otherHost, "PropertyOwner");
        var response = await client.PostAsJsonAsync("/api/service-requests", new
        {
            propertyId,
            supplierOrgId,
            category = "cleaning",
        });

        Assert.True(response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task GetById_AsUnlinkedUserWithMatchingSupplierEmail_DoesNotBindSupplierOrg()
    {
        var (hostId, _, propertyId, supplierOrgId, _) = await SeedScenarioAsync();
        var serviceRequestId = await CreateServiceRequestAsync(hostId, propertyId, supplierOrgId);
        var supplierEmail = await GetSupplierEmailAsync(supplierOrgId);
        var attackerId = $"auth0|email-shadow-{Guid.NewGuid():N}";
        await SeedUnlinkedUserAsync(attackerId, supplierEmail);

        using var attackerClient = _factory.CreateAuthenticatedClient(
            attackerId,
            roles: "PropertyOwner",
            email: supplierEmail);

        var response = await attackerClient.GetAsync($"/api/service-requests/{serviceRequestId}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        await AssertUserSupplierOrgIdAsync(attackerId, expectedSupplierOrgId: null);
    }

    [Fact]
    public async Task Take_AsUnlinkedSupplierWithMatchingEmail_DoesNotBindSupplierOrg()
    {
        var (hostId, _, propertyId, supplierOrgId, _) = await SeedScenarioAsync();
        var serviceRequestId = await CreateServiceRequestAsync(hostId, propertyId, supplierOrgId);
        var supplierEmail = await GetSupplierEmailAsync(supplierOrgId);
        var attackerId = $"auth0|supplier-shadow-{Guid.NewGuid():N}";
        await SeedUnlinkedUserAsync(attackerId, supplierEmail);

        using var attackerClient = _factory.CreateAuthenticatedClient(
            attackerId,
            roles: "Supplier",
            email: supplierEmail);

        var response = await attackerClient.PostAsJsonAsync($"/api/service-requests/{serviceRequestId}/take", new { });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        await AssertUserSupplierOrgIdAsync(attackerId, expectedSupplierOrgId: null);
    }

    [Fact]
    public async Task List_Unauthenticated_Returns401()
    {
        using var client = _factory.CreateClient();
        var response = await client.GetAsync("/api/service-requests");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task List_AsSameOrgPropertyOwner_DoesNotReturnOtherOwnersRequests()
    {
        var (hostId, visibleRequestId, hiddenRequestId) = await SeedSameOrgRestrictedRequestsAsync();
        using var client = _factory.CreateAuthenticatedClient(hostId, "PropertyOwner");

        var response = await client.GetAsync("/api/service-requests");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var ids = body.GetProperty("items")
            .EnumerateArray()
            .Select(item => item.GetProperty("id").GetGuid())
            .ToArray();
        Assert.Contains(visibleRequestId, ids);
        Assert.DoesNotContain(hiddenRequestId, ids);
        Assert.Equal(1, body.GetProperty("total").GetInt32());
    }

    [Fact]
    public async Task GetById_AsSameOrgPropertyOwner_ForOtherOwnersRequest_Returns404()
    {
        var (hostId, _, hiddenRequestId) = await SeedSameOrgRestrictedRequestsAsync();
        using var client = _factory.CreateAuthenticatedClient(hostId, "PropertyOwner");

        var response = await client.GetAsync($"/api/service-requests/{hiddenRequestId}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Reject_FromRichiesto_SetsRifiutato()
    {
        var (hostId, _, propertyId, supplierOrgId, supplierUserId) = await SeedScenarioAsync();

        using var hostClient = _factory.CreateAuthenticatedClient(hostId, "PropertyOwner");
        var create = await hostClient.PostAsJsonAsync("/api/service-requests", new
        {
            propertyId,
            supplierOrgId,
            category = "cleaning",
        });
        var created = await create.Content.ReadFromJsonAsync<JsonElement>();
        var id = created.GetProperty("id").GetGuid();

        using var supplierClient = _factory.CreateAuthenticatedClient(supplierUserId, "Supplier");
        var reject = await supplierClient.PostAsJsonAsync($"/api/service-requests/{id}/reject", new { reason = "Non disponibile" });

        Assert.Equal(HttpStatusCode.OK, reject.StatusCode);
        var body = await reject.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Rifiutato", body.GetProperty("status").GetString());
    }

    private async Task<Guid> CreateServiceRequestAsync(string hostId, Guid propertyId, Guid supplierOrgId)
    {
        using var hostClient = _factory.CreateAuthenticatedClient(hostId, "PropertyOwner");
        var create = await hostClient.PostAsJsonAsync("/api/service-requests", new
        {
            propertyId,
            supplierOrgId,
            category = "cleaning",
        });
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        var body = await create.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("id").GetGuid();
    }

    private async Task<string> GetSupplierEmailAsync(Guid supplierOrgId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.SupplierProfiles
            .AsNoTracking()
            .Where(sp => sp.OrgId == supplierOrgId)
            .Select(sp => sp.Email)
            .SingleAsync();
    }

    private async Task SeedUnlinkedUserAsync(string userId, string email)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.Users.Add(new User
        {
            Id = userId,
            Email = email,
            FirstName = "Shadow",
            LastName = "Supplier",
            IsActive = true,
        });
        await db.SaveChangesAsync();
    }

    private async Task AssertUserSupplierOrgIdAsync(string userId, Guid? expectedSupplierOrgId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var supplierOrgId = await db.Users
            .Where(u => u.Id == userId)
            .Select(u => u.SupplierOrgId)
            .SingleAsync();
        Assert.Equal(expectedSupplierOrgId, supplierOrgId);
    }

    private async Task<(string HostId, Guid HostOrgId, Guid PropertyId, Guid SupplierOrgId, string SupplierUserId)> SeedScenarioAsync()
    {
        const string comune = "H501";
        var hostId = $"auth0|host-{Guid.NewGuid():N}";
        var hostOrg = await _factory.SeedOrgForOwnerAsync(hostId);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var property = new Property
        {
            OwnerId = hostId,
            OrgId = hostOrg.Id,
            Name = "SR Test Property",
            Address = $"Via SR {Guid.NewGuid():N}",
            City = comune,
            PostalCode = "00100",
            Bedrooms = 2,
            Bathrooms = 1,
            MaxGuests = 4,
            NightlyRate = 100m,
            CinCode = "IT-ABC123-DEF456",
            IsActive = true,
        };
        db.Properties.Add(property);

        var supplierOrg = new OrgEntity
        {
            Name = "SR Supplier",
            Slug = $"sr-sup-{Guid.NewGuid():N}"[..25],
            DisplayName = "SR Supplier",
            ContactEmail = "sr-supplier@test.com",
            OrgType = OrgType.Supplier,
            PlanTier = PlanTier.Starter,
        };
        db.Orgs.Add(supplierOrg);

        var supplierUserId = $"auth0|supplier-{Guid.NewGuid():N}";
        db.Users.Add(new User
        {
            Id = supplierUserId,
            Email = supplierOrg.ContactEmail,
            FirstName = "SR",
            LastName = "Supplier",
            OrgId = supplierOrg.Id,
            SupplierOrgId = supplierOrg.Id,
            IsActive = true,
        });

        db.SupplierProfiles.Add(new SupplierProfile
        {
            OrgId = supplierOrg.Id,
            Email = supplierOrg.ContactEmail,
            LegalName = "SR Supplier Srl",
            Phone = "+39 06 111111",
            Status = SupplierStatus.Active,
            ComuniJson = $"[\"{comune}\"]",
            CategoriesJson = "[\"cleaning\"]",
            TosAcceptedAt = DateTime.UtcNow,
        });

        await db.SaveChangesAsync();
        return (hostId, hostOrg.Id, property.Id, supplierOrg.Id, supplierUserId);
    }

    private async Task<(string HostId, Guid VisibleRequestId, Guid HiddenRequestId)> SeedSameOrgRestrictedRequestsAsync()
    {
        var (hostId, hostOrgId, visiblePropertyId, supplierOrgId, _) = await SeedScenarioAsync();
        var otherOwnerId = $"auth0|same-org-owner-{Guid.NewGuid():N}";

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        db.Users.Add(new User
        {
            Id = otherOwnerId,
            Email = $"{Guid.NewGuid():N}@example.com",
            FirstName = "Other",
            LastName = "Owner",
            OrgId = hostOrgId,
            IsActive = true,
        });

        var hiddenProperty = new Property
        {
            OwnerId = otherOwnerId,
            OrgId = hostOrgId,
            Name = "Hidden SR Property",
            Address = $"Via Hidden {Guid.NewGuid():N}",
            City = "H501",
            PostalCode = "00100",
            Bedrooms = 2,
            Bathrooms = 1,
            MaxGuests = 4,
            NightlyRate = 100m,
            CinCode = "IT-DEF123-GHI456",
            IsActive = true,
        };
        db.Properties.Add(hiddenProperty);

        var visibleRequest = new ServiceRequest
        {
            OrgId = hostOrgId,
            PropertyId = visiblePropertyId,
            SupplierOrgId = supplierOrgId,
            Category = "cleaning",
            Notes = "Visible owner request",
            CreatedAt = DateTime.UtcNow.AddMinutes(-1),
            UpdatedAt = DateTime.UtcNow.AddMinutes(-1),
        };

        var hiddenRequest = new ServiceRequest
        {
            OrgId = hostOrgId,
            PropertyId = hiddenProperty.Id,
            SupplierOrgId = supplierOrgId,
            Category = "cleaning",
            Notes = "Hidden owner request",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };

        db.ServiceRequests.AddRange(visibleRequest, hiddenRequest);
        await db.SaveChangesAsync();

        return (hostId, visibleRequest.Id, hiddenRequest.Id);
    }
}
