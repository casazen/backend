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
using PlanTierEnum = Casazen.Core.Entities.Enums.PlanTier;

namespace Casazen.Tests.Integration;

/// <summary>
/// BK-03 (A3-02, R-05, A8-23) on PostgreSQL with the migrated rates: the checkout quote and the booking use the same
/// tourist tax engine and the same <c>TouristTaxRates</c>, so the amount shown is the amount recorded and charged.
/// </summary>
public class TouristTaxQuoteIntegrationTests : IClassFixture<CasazenWebApplicationFactory>
{
    private const string ConsentVersion = "2026-06-direct-checkout-v1";
    private readonly CasazenWebApplicationFactory _factory;

    public TouristTaxQuoteIntegrationTests(CasazenWebApplicationFactory factory)
    {
        _factory = factory;
        FakeStripeService.Reset();
    }

    [PostgresFact]
    public async Task Quote_ThenBook_RecordsAndChargesTheQuotedTouristTax()
    {
        // City typed in upper case with spaces: the seeded "Firenze" rate (6,00, exempt under 12) is still found.
        var property = await SeedPropertyAsync("  FIRENZE ");
        using var client = _factory.CreateClient();
        var (checkIn, checkOut) = Stay(nights: 3);

        var quoteResponse = await client.PostAsJsonAsync("/api/public/bookings/quote", new
        {
            propertyId = property.Id,
            checkInDate = checkIn,
            checkOutDate = checkOut,
            numberOfAdults = 2,
            numberOfChildren = 1,
            childrenAges = new[] { 8 },
        });

        Assert.Equal(HttpStatusCode.OK, quoteResponse.StatusCode);
        var quote = await ReadJsonAsync(quoteResponse);
        var tax = quote.GetProperty("touristTax");
        Assert.Equal("Calculated", tax.GetProperty("status").GetString());
        // 2 adults x 6,00 x 3 nights; the 8-year-old is exempt.
        Assert.Equal(36.00m, tax.GetProperty("amount").GetDecimal());
        Assert.True(tax.GetProperty("ageRulesApply").GetBoolean());
        Assert.Equal(500.00m, quote.GetProperty("basePrice").GetDecimal());
        Assert.Equal(536.00m, quote.GetProperty("totalPrice").GetDecimal());

        var bookingResponse = await client.PostAsJsonAsync(
            "/api/public/bookings",
            BookingPayload(property.Id, checkIn, checkOut, children: 1, childrenAges: [8]));

        Assert.Equal(HttpStatusCode.OK, bookingResponse.StatusCode);
        var booked = await ReadJsonAsync(bookingResponse);
        Assert.Equal(tax.GetProperty("amount").GetDecimal(), booked.GetProperty("touristTaxAmount").GetDecimal());
        Assert.Equal(quote.GetProperty("totalPrice").GetDecimal(), booked.GetProperty("amount").GetDecimal());
        Assert.Equal("Calculated", booked.GetProperty("touristTaxStatus").GetString());

        var bookingId = booked.GetProperty("bookingId").GetGuid();
        var stored = await WithDbAsync(db => db.Bookings.IgnoreQueryFilters().AsNoTracking().SingleAsync(b => b.Id == bookingId));
        Assert.Equal(36.00m, stored.TouristTax);
        Assert.Equal(36.00m, stored.TouristTaxAmount);
        Assert.Equal(536.00m, stored.TotalPrice);
        var payment = await WithDbAsync(db => db.Payments.IgnoreQueryFilters().AsNoTracking().SingleAsync(p => p.BookingId == bookingId));
        Assert.Equal(536.00m, payment.Amount);
    }

    [PostgresFact]
    public async Task Quote_RateStoredInLowerCase_IsFoundForTheCapitalizedCity()
    {
        // A8-23: an admin typing "lecco" must not make the rate invisible to "Lecco".
        await WithDbAsync(async db =>
        {
            db.TouristTaxRates.Add(new TouristTaxRate
            {
                City = "lecco",
                RegionCode = "LOM",
                RatePerPersonPerNight = 1.50m,
                MaxNights = 5,
                MinimumAge = 14,
                IsActive = true,
                EffectiveFrom = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            });
            return await db.SaveChangesAsync();
        });
        var property = await SeedPropertyAsync("Lecco");
        using var client = _factory.CreateClient();
        var (checkIn, checkOut) = Stay(nights: 2);

        var response = await client.PostAsJsonAsync("/api/public/bookings/quote", new
        {
            propertyId = property.Id,
            checkInDate = checkIn,
            checkOutDate = checkOut,
            numberOfAdults = 2,
            numberOfChildren = 0,
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var tax = (await ReadJsonAsync(response)).GetProperty("touristTax");
        Assert.Equal("Calculated", tax.GetProperty("status").GetString());
        Assert.Equal(6.00m, tax.GetProperty("amount").GetDecimal());
    }

    [PostgresFact]
    public async Task Quote_ComuneWithoutRate_IsUnavailableAndTheBookingGoesOnWithoutTax()
    {
        // Seveso: no rate in CasaZen. No invented amount, no blocked checkout.
        var property = await SeedPropertyAsync("Seveso");
        using var client = _factory.CreateClient();
        var (checkIn, checkOut) = Stay(nights: 2);

        var quoteResponse = await client.PostAsJsonAsync("/api/public/bookings/quote", new
        {
            propertyId = property.Id,
            checkInDate = checkIn,
            checkOutDate = checkOut,
            numberOfAdults = 2,
            numberOfChildren = 0,
        });

        var quote = await ReadJsonAsync(quoteResponse);
        Assert.Equal("RateUnavailable", quote.GetProperty("touristTax").GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, quote.GetProperty("touristTax").GetProperty("amount").ValueKind);
        Assert.Equal(350.00m, quote.GetProperty("totalPrice").GetDecimal());

        var bookingResponse = await client.PostAsJsonAsync(
            "/api/public/bookings",
            BookingPayload(property.Id, checkIn, checkOut, paymentOption: "OnSite"));

        Assert.Equal(HttpStatusCode.OK, bookingResponse.StatusCode);
        var booked = await ReadJsonAsync(bookingResponse);
        Assert.Equal("RateUnavailable", booked.GetProperty("touristTaxStatus").GetString());
        Assert.Equal(0m, booked.GetProperty("touristTaxAmount").GetDecimal());
        Assert.Equal(350.00m, booked.GetProperty("amount").GetDecimal());
    }

    [PostgresFact]
    public async Task Book_MinorWithoutAgeWhereTheRateDependsOnIt_Returns422AndStoresNothing()
    {
        var property = await SeedPropertyAsync("Firenze");
        using var client = _factory.CreateClient();
        var (checkIn, checkOut) = Stay(nights: 2);

        var response = await client.PostAsJsonAsync(
            "/api/public/bookings",
            BookingPayload(property.Id, checkIn, checkOut, children: 1));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("tourist_tax_child_ages_required", (await ReadJsonAsync(response)).GetProperty("code").GetString());
        Assert.False(await WithDbAsync(db => db.Bookings.IgnoreQueryFilters().AnyAsync(b => b.PropertyId == property.Id)));
        Assert.False(await WithDbAsync(db => db.Guests.IgnoreQueryFilters().AnyAsync(g => g.OrgId == property.OrgId)));
    }

    [PostgresFact]
    public async Task Quote_AgesNotMatchingTheChildren_Returns400()
    {
        var property = await SeedPropertyAsync("Firenze");
        using var client = _factory.CreateClient();
        var (checkIn, checkOut) = Stay(nights: 2);

        var response = await client.PostAsJsonAsync("/api/public/bookings/quote", new
        {
            propertyId = property.Id,
            checkInDate = checkIn,
            checkOutDate = checkOut,
            numberOfAdults = 1,
            numberOfChildren = 2,
            childrenAges = new[] { 5 },
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [PostgresFact]
    public async Task Quote_UnknownProperty_Returns404()
    {
        using var client = _factory.CreateClient();
        var (checkIn, checkOut) = Stay(nights: 2);

        var response = await client.PostAsJsonAsync("/api/public/bookings/quote", new
        {
            propertyId = Guid.NewGuid(),
            checkInDate = checkIn,
            checkOutDate = checkOut,
            numberOfAdults = 1,
            numberOfChildren = 0,
        });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    /// <summary>A stay a few months ahead, far from the start dates of the seeded rates.</summary>
    private static (string CheckIn, string CheckOut) Stay(int nights)
    {
        var checkIn = DateTime.UtcNow.Date.AddDays(60);
        return (checkIn.ToString("yyyy-MM-dd"), checkIn.AddDays(nights).ToString("yyyy-MM-dd"));
    }

    private static object BookingPayload(
        Guid propertyId,
        string checkIn,
        string checkOut,
        int children = 0,
        int[]? childrenAges = null,
        string paymentOption = "Immediate") => new
        {
            propertyId,
            checkInDate = checkIn,
            checkOutDate = checkOut,
            numberOfAdults = 2,
            numberOfChildren = children,
            childrenAges,
            guest = new
            {
                firstName = "Mario",
                lastName = "Rossi",
                email = $"mario.{Guid.NewGuid():N}@example.com",
                phone = "+393331234567",
                country = "IT",
            },
            consent = new { dataProcessing = true, consentVersion = ConsentVersion },
            paymentOption,
        };

    private async Task<Property> SeedPropertyAsync(string city)
    {
        return await WithDbAsync(async db =>
        {
            var org = new OrgEntity
            {
                Name = "Tourist Tax Org",
                Slug = $"tourist-tax-{Guid.NewGuid():N}",
                DisplayName = "Tourist Tax Org",
                ContactEmail = "tax@example.com",
                PlanTier = PlanTierEnum.Starter,
                IsActive = true,
                StripeConnectedAccountId = "acct_test_connect_ready",
                ConnectChargesEnabled = true,
            };
            db.Orgs.Add(org);

            var property = new Property
            {
                OwnerId = $"auth0|owner-{Guid.NewGuid():N}",
                OrgId = org.Id,
                Name = "Tourist Tax Villa",
                Description = "Integration test property",
                Address = $"Via Tassa {Guid.NewGuid():N}",
                City = city,
                PostalCode = "50100",
                Bedrooms = 2,
                Bathrooms = 1,
                MaxGuests = 4,
                NightlyRate = 150m,
                CleaningFee = 50m,
                CinCode = "IT048017C2ABCDEFGH",
                IsActive = true,
                ComplianceStatus = PropertyComplianceStatus.Active,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            };
            db.Properties.Add(property);
            await db.SaveChangesAsync();
            return property;
        });
    }

    private async Task<T> WithDbAsync<T>(Func<AppDbContext, Task<T>> action)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await action(db);
    }

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response)
    {
        var json = await response.Content.ReadAsStringAsync();
        return JsonDocument.Parse(json).RootElement.Clone();
    }
}
