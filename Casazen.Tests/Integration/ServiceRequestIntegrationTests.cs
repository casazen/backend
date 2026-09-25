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
// OrgEntity alias from Casazen.Tests.csproj

namespace Casazen.Tests.Integration;

public class ServiceRequestIntegrationTests : IClassFixture<CasazenWebApplicationFactory>
{
    private readonly CasazenWebApplicationFactory _factory;

    public ServiceRequestIntegrationTests(CasazenWebApplicationFactory factory) => _factory = factory;

    [Fact]
    public async Task Create_AsHostForStayOfTheProperty_Returns201WithBooking()
    {
        var s = await SeedScenarioAsync();
        using var client = _factory.CreateAuthenticatedClient(s.HostId, "PropertyOwner");

        var response = await client.PostAsJsonAsync("/api/service-requests", new
        {
            propertyId = s.PropertyId,
            bookingId = s.BookingId,
            supplierOrgId = s.SupplierOrgId,
            category = "cleaning",
            notes = "Turnover dopo checkout",
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Richiesto", body.GetProperty("status").GetString());
        Assert.Equal(s.BookingId, body.GetProperty("bookingId").GetGuid());
        Assert.Equal("ShortRent", body.GetProperty("rentalContext").GetString());
    }

    [Fact]
    public async Task Create_WithChargeToGuest_Returns422WithoutCreating()
    {
        var s = await SeedScenarioAsync();
        using var client = _factory.CreateAuthenticatedClient(s.HostId, "PropertyOwner");

        var response = await client.PostAsJsonAsync("/api/service-requests", new
        {
            propertyId = s.PropertyId,
            bookingId = s.BookingId,
            supplierOrgId = s.SupplierOrgId,
            category = "cleaning",
            chargeToGuest = true,
        });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("service_request_charge_to_guest_not_allowed", problem.GetProperty("code").GetString());
        Assert.Equal(0, await CountRequestsAsync(s.PropertyId));
    }

    [Fact]
    public async Task GetInbox_AsSupplier_ReturnsCreatedRequest()
    {
        var s = await SeedScenarioAsync();
        var id = await CreateServiceRequestAsync(s);

        using var supplierClient = _factory.CreateAuthenticatedClient(s.SupplierUserId, "Supplier");
        var inbox = await supplierClient.GetAsync("/api/supplier/inbox");

        Assert.Equal(HttpStatusCode.OK, inbox.StatusCode);
        var body = await inbox.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Contains(id, body.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetGuid()));
    }

    [Fact]
    public async Task CompleteFlow_TakeCompleteMarkPaid_Succeeds()
    {
        var s = await SeedScenarioAsync();
        var id = await CreateServiceRequestAsync(s);

        using var hostClient = _factory.CreateAuthenticatedClient(s.HostId, "PropertyOwner");
        using var supplierClient = _factory.CreateAuthenticatedClient(s.SupplierUserId, "Supplier");
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
    public async Task Create_AsOtherOrgHost_Returns404()
    {
        var s = await SeedScenarioAsync();
        var otherHost = $"auth0|other-{Guid.NewGuid():N}";
        await _factory.SeedOrgForOwnerAsync(otherHost);

        using var client = _factory.CreateAuthenticatedClient(otherHost, "PropertyOwner");
        var response = await client.PostAsJsonAsync("/api/service-requests", new
        {
            propertyId = s.PropertyId,
            bookingId = s.BookingId,
            supplierOrgId = s.SupplierOrgId,
            category = "cleaning",
        });

        // TN-3: another org's property is not visible to the caller (tenant filter), so it answers 404.
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(0, await CountRequestsAsync(s.PropertyId));
    }

    [Fact]
    public async Task GetById_AsUnlinkedUserWithMatchingSupplierEmail_DoesNotBindSupplierOrg()
    {
        var s = await SeedScenarioAsync();
        var serviceRequestId = await CreateServiceRequestAsync(s);
        var supplierEmail = await GetSupplierEmailAsync(s.SupplierOrgId);
        var attackerId = $"auth0|email-shadow-{Guid.NewGuid():N}";
        await SeedUnlinkedUserAsync(attackerId, supplierEmail);
        // A real host of another org (onboarding completed, PL-02), not a user stopped by the onboarding gate.
        await _factory.SeedOrgForOwnerAsync(attackerId);

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
        var s = await SeedScenarioAsync();
        var serviceRequestId = await CreateServiceRequestAsync(s);
        var supplierEmail = await GetSupplierEmailAsync(s.SupplierOrgId);
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
        var s = await SeedScenarioAsync();
        var id = await CreateServiceRequestAsync(s);

        using var supplierClient = _factory.CreateAuthenticatedClient(s.SupplierUserId, "Supplier");
        var reject = await supplierClient.PostAsJsonAsync($"/api/service-requests/{id}/reject", new { reason = "Non disponibile" });

        Assert.Equal(HttpStatusCode.OK, reject.StatusCode);
        var body = await reject.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Rifiutato", body.GetProperty("status").GetString());
    }

    private async Task<Guid> CreateServiceRequestAsync(Scenario s)
    {
        using var hostClient = _factory.CreateAuthenticatedClient(s.HostId, "PropertyOwner");
        var create = await hostClient.PostAsJsonAsync("/api/service-requests", new
        {
            propertyId = s.PropertyId,
            bookingId = s.BookingId,
            supplierOrgId = s.SupplierOrgId,
            category = "cleaning",
        });
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        var body = await create.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("id").GetGuid();
    }

    private async Task<int> CountRequestsAsync(Guid propertyId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.ServiceRequests.IgnoreQueryFilters().CountAsync(r => r.PropertyId == propertyId);
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

    /// <summary>
    /// Host org with one property (comune H501) and one confirmed stay on it, plus an active supplier (its own org)
    /// operating in H501. Short-rent requests are created for that stay (D2).
    /// </summary>
    private async Task<Scenario> SeedScenarioAsync()
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

        var guest = new Guest
        {
            OrgId = hostOrg.Id,
            FirstName = "Giulia",
            LastName = "Ospite",
            Email = $"sr-{Guid.NewGuid():N}@example.com",
        };
        var booking = new Booking
        {
            OrgId = hostOrg.Id,
            PropertyId = property.Id,
            GuestId = guest.Id,
            CheckInDate = TimeProvider.System.TodayInRome().AddDays(7),
            CheckOutDate = TimeProvider.System.TodayInRome().AddDays(10),
            NumberOfGuests = 2,
            Status = BookingStatus.Confirmed,
            Source = BookingSource.Direct,
            BasePrice = 300m,
            TotalPrice = 300m,
        };
        db.AddRange(guest, booking);

        var supplierOrg = new OrgEntity
        {
            Name = "SR Supplier",
            Slug = $"sr-sup-{Guid.NewGuid():N}"[..25],
            DisplayName = "SR Supplier",
            // One supplier profile per email (SU-14): every scenario gets its own.
            ContactEmail = $"sr-supplier-{Guid.NewGuid():N}@test.com",
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
        return new Scenario(hostId, hostOrg.Id, property.Id, booking.Id, supplierOrg.Id, supplierUserId);
    }

    private sealed record Scenario(
        string HostId,
        Guid HostOrgId,
        Guid PropertyId,
        Guid BookingId,
        Guid SupplierOrgId,
        string SupplierUserId);

    private async Task<(string HostId, Guid VisibleRequestId, Guid HiddenRequestId)> SeedSameOrgRestrictedRequestsAsync()
    {
        var scenario = await SeedScenarioAsync();
        var (hostId, hostOrgId, visiblePropertyId, supplierOrgId) =
            (scenario.HostId, scenario.HostOrgId, scenario.PropertyId, scenario.SupplierOrgId);
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
