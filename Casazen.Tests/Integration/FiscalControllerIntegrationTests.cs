using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Casazen.Core.Entities;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Casazen.Tests.Integration;

public class FiscalControllerIntegrationTests : IClassFixture<CasazenWebApplicationFactory>
{
    private readonly CasazenWebApplicationFactory _factory;

    public FiscalControllerIntegrationTests(CasazenWebApplicationFactory factory) => _factory = factory;

    [Fact]
    public async Task GetRegime_OneProperty_RecommendsCedolare21()
    {
        var owner = $"auth0|fiscal-{Guid.NewGuid():N}";
        var property = await _factory.SeedPropertyAsync(owner);
        // Only apartments with short-term stays in the tax year count toward the threshold (CO-18, A5-22).
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.Bookings.Add(new Booking
            {
                PropertyId = property.Id,
                OrgId = property.OrgId,
                Guest = new Guest
                {
                    OrgId = property.OrgId,
                    FirstName = "Regime",
                    LastName = "Guest",
                    Email = $"regime-{Guid.NewGuid():N}@example.com",
                    Country = "IT",
                },
                CheckInDate = new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc),
                CheckOutDate = new DateTime(2026, 5, 4, 0, 0, 0, DateTimeKind.Utc),
                NumberOfGuests = 1,
                NumberOfAdults = 1,
                Status = BookingStatus.Confirmed,
                Source = BookingSource.Direct,
                BasePrice = 300m,
                TotalPrice = 300m,
            });
            await db.SaveChangesAsync();
        }

        using var client = _factory.CreateAuthenticatedClient(owner, "PropertyOwner");
        var response = await client.GetAsync("/api/fiscal/regime?taxYear=2026");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, body.GetProperty("strPropertyCount").GetInt32());
        Assert.False(body.GetProperty("requiresPartitaIva").GetBoolean());
        Assert.Equal("CedolareSecca21", body.GetProperty("properties")[0].GetProperty("recommendedRegime").GetString());
        Assert.Contains("informativa", body.GetProperty("disclaimer").GetString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AssignRegime_ForeignProperty_Returns404()
    {
        var ownerA = $"auth0|fiscal-a-{Guid.NewGuid():N}";
        var ownerB = $"auth0|fiscal-b-{Guid.NewGuid():N}";
        var property = await _factory.SeedPropertyAsync(ownerA);
        await _factory.SeedOrgForOwnerAsync(ownerB);

        using var client = _factory.CreateAuthenticatedClient(ownerB, "PropertyOwner");
        var response = await client.PutAsJsonAsync($"/api/fiscal/properties/{property.Id}/regime", new
        {
            taxYear = 2026,
            regime = "CedolareSecca21",
            isPrimaryForCedolare = true,
        });
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task AnnualReport_ContainsPackLabelNotTaxDue()
    {
        var owner = $"auth0|fiscal-r-{Guid.NewGuid():N}";
        await _factory.SeedPropertyAsync(owner);
        using var client = _factory.CreateAuthenticatedClient(owner, "PropertyOwner");
        var response = await client.GetAsync("/api/fiscal/reports/annual/2026");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadAsStringAsync();
        Assert.Contains("commercialista", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("irpef", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("taxDue", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AnnualReport_IncludesSettledPaymentsForInactiveProperties()
    {
        var owner = $"auth0|fiscal-inactive-{Guid.NewGuid():N}";
        var property = await _factory.SeedPropertyAsync(owner);

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var trackedProperty = await db.Properties.SingleAsync(p => p.Id == property.Id);
            trackedProperty.IsActive = false;

            var guest = new Guest
            {
                OrgId = property.OrgId,
                FirstName = "Inactive",
                LastName = "Guest",
                Email = $"inactive-{Guid.NewGuid():N}@example.com",
                Country = "IT",
            };
            var booking = new Booking
            {
                PropertyId = property.Id,
                OrgId = property.OrgId,
                Guest = guest,
                CheckInDate = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc),
                CheckOutDate = new DateTime(2026, 6, 3, 0, 0, 0, DateTimeKind.Utc),
                NumberOfGuests = 1,
                NumberOfAdults = 1,
                Status = BookingStatus.Confirmed,
                Source = BookingSource.Direct,
                BasePrice = 240m,
                TotalPrice = 250m,
            };
            db.Bookings.Add(booking);
            db.Payments.Add(new Payment
            {
                Booking = booking,
                OrgId = property.OrgId,
                Amount = 250m,
                Status = PaymentStatus.Completed,
                Method = PaymentMethod.CreditCard,
                ProcessedAt = new DateTime(2026, 6, 2, 0, 0, 0, DateTimeKind.Utc),
                NetAmountAfterWithholding = 250m,
            });

            await db.SaveChangesAsync();
        }

        using var client = _factory.CreateAuthenticatedClient(owner, "PropertyOwner");
        var response = await client.GetAsync("/api/fiscal/reports/annual/2026");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var reportProperty = body.GetProperty("properties").EnumerateArray()
            .Single(p => p.GetProperty("propertyId").GetGuid() == property.Id);
        Assert.Equal(250m, reportProperty.GetProperty("grossIncome").GetDecimal());
        Assert.Equal(250m, body.GetProperty("totals").GetProperty("grossIncome").GetDecimal());
    }

    [Fact]
    public async Task AnnualReportCsv_HasCsvContentType()
    {
        var owner = $"auth0|fiscal-csv-{Guid.NewGuid():N}";
        await _factory.SeedPropertyAsync(owner);
        using var client = _factory.CreateAuthenticatedClient(owner, "PropertyOwner");
        var response = await client.GetAsync("/api/fiscal/reports/annual/2026?format=csv");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/csv", response.Content.Headers.ContentType?.MediaType);
        Assert.DoesNotContain("FiscalCode", response.Content.Headers.ContentDisposition?.FileName ?? "", StringComparison.OrdinalIgnoreCase);
    }
}
