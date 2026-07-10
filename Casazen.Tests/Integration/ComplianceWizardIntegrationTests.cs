using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Enums;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Casazen.Tests.Integration;

public class ComplianceWizardIntegrationTests : IClassFixture<CasazenWebApplicationFactory>
{
    private readonly CasazenWebApplicationFactory _factory;

    public ComplianceWizardIntegrationTests(CasazenWebApplicationFactory factory) => _factory = factory;

    [Fact]
    public async Task PendingProperty_NotInPublicOrgList()
    {
        var (hostId, org, propertyId) = await SeedPropertyScenarioAsync(PropertyComplianceStatus.Pending);
        using var client = _factory.CreateClient();

        var response = await client.GetAsync($"/api/public/orgs/{org.Slug}/properties");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(JsonValueKind.Array, body.ValueKind);
        Assert.Empty(body.EnumerateArray());
    }

    [Fact]
    public async Task ActiveProperty_AppearsInPublicOrgList()
    {
        var (hostId, org, propertyId) = await SeedPropertyScenarioAsync(PropertyComplianceStatus.Active);
        using var client = _factory.CreateClient();

        var response = await client.GetAsync($"/api/public/orgs/{org.Slug}/properties");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var ids = body.EnumerateArray().Select(p => p.GetProperty("id").GetGuid()).ToList();
        Assert.Contains(propertyId, ids);
    }

    [Fact]
    public async Task CheckoutWizard_CompletesBooking()
    {
        var (hostId, org, propertyId) = await SeedPropertyScenarioAsync(PropertyComplianceStatus.Active);
        var bookingId = await SeedCheckedInBookingAsync(hostId, org.Id, propertyId);

        using var client = _factory.CreateAuthenticatedClient(hostId, "PropertyOwner");
        var start = await client.PostAsync($"/api/bookings/{bookingId}/checkout-wizard/start", null);
        Assert.Equal(HttpStatusCode.OK, start.StatusCode);

        var complete = await client.PostAsJsonAsync($"/api/bookings/{bookingId}/checkout-wizard/complete", new
        {
            confirmDeparture = true,
        });
        Assert.Equal(HttpStatusCode.OK, complete.StatusCode);

        var body = await complete.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(body.GetProperty("propertyReady").GetBoolean());
        Assert.Equal("CheckedOut", body.GetProperty("bookingStatus").GetString());
    }

    [Fact]
    public async Task CheckoutWizard_WithSupplier_CreatesServiceRequest()
    {
        var (hostId, org, propertyId) = await SeedPropertyScenarioAsync(PropertyComplianceStatus.Active);
        var bookingId = await SeedCheckedInBookingAsync(hostId, org.Id, propertyId);
        var supplierOrgId = await SeedSupplierAsync("Rome");

        using var client = _factory.CreateAuthenticatedClient(hostId, "PropertyOwner");
        await client.PostAsync($"/api/bookings/{bookingId}/checkout-wizard/start", null);

        var complete = await client.PostAsJsonAsync($"/api/bookings/{bookingId}/checkout-wizard/complete", new
        {
            confirmDeparture = true,
            supplierOrgId,
            serviceNotes = "Pulizia post checkout",
            serviceCategory = "cleaning",
        });
        Assert.Equal(HttpStatusCode.OK, complete.StatusCode);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var request = await db.ServiceRequests
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(r => r.BookingId == bookingId);
        Assert.NotNull(request);
        Assert.Equal(supplierOrgId, request!.SupplierOrgId);
    }

    private async Task<(string HostId, Core.Entities.Org Org, Guid PropertyId)> SeedPropertyScenarioAsync(
        PropertyComplianceStatus complianceStatus)
    {
        var hostId = $"auth0|host-{Guid.NewGuid():N}";
        var org = await _factory.SeedOrgForOwnerAsync(hostId);
        org.Slug = $"slug-{Guid.NewGuid():N}";
        org.StripeConnectedAccountId = "acct_test";
        org.ConnectChargesEnabled = true;

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.Orgs.Update(org);

        var property = new Property
        {
            OwnerId = hostId,
            OrgId = org.Id,
            Name = "Compliance Integration Villa",
            Description = "Test",
            Address = $"Via Test {Guid.NewGuid():N}",
            City = "Rome",
            PostalCode = "00100",
            Latitude = 41.9m,
            Longitude = 12.5m,
            Bedrooms = 2,
            Bathrooms = 1,
            MaxGuests = 4,
            NightlyRate = 120m,
            CinCode = "IT-12345-0123456789",
            IsActive = true,
            ComplianceStatus = complianceStatus,
        };

        db.Properties.Add(property);
        await db.SaveChangesAsync();
        return (hostId, org, property.Id);
    }

    private async Task<Guid> SeedCheckedInBookingAsync(string hostId, Guid orgId, Guid propertyId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var guest = new Guest
        {
            FirstName = "Luigi",
            LastName = "Verdi",
            Email = $"guest-{Guid.NewGuid():N}@test.com",
        };
        db.Guests.Add(guest);

        var booking = new Booking
        {
            PropertyId = propertyId,
            OrgId = orgId,
            GuestId = guest.Id,
            CheckInDate = DateTime.UtcNow.Date.AddDays(-2),
            CheckOutDate = DateTime.UtcNow.Date,
            Status = BookingStatus.CheckedIn,
            NumberOfGuests = 2,
            BasePrice = 200,
            TouristTax = 10,
            TotalPrice = 210,
        };
        db.Bookings.Add(booking);
        await db.SaveChangesAsync();
        return booking.Id;
    }

    private async Task<Guid> SeedSupplierAsync(string comune)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var org = new OrgEntity
        {
            Name = "Supplier Org",
            Slug = $"supplier-{Guid.NewGuid():N}",
            OrgType = OrgType.Supplier,
        };
        db.Orgs.Add(org);

        db.SupplierProfiles.Add(new SupplierProfile
        {
            OrgId = org.Id,
            LegalName = "Pulizie SRL",
            Email = $"supplier-{Guid.NewGuid():N}@test.com",
            Phone = "+3906123456",
            Status = SupplierStatus.Active,
            CategoriesJson = """["cleaning"]""",
            ComuniJson = $$"""["{{comune}}"]""",
            TosAcceptedAt = DateTime.UtcNow,
        });

        await db.SaveChangesAsync();
        return org.Id;
    }
}
