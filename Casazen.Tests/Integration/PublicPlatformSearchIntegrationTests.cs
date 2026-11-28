using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Utilities;
using Casazen.Infrastructure.Data;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Casazen.Tests.Integration;

public class PublicPlatformSearchIntegrationTests : IClassFixture<CasazenWebApplicationFactory>
{
    private const string ConsentVersion = "2026-06-direct-checkout-v1";

    private readonly CasazenWebApplicationFactory _factory;

    public PublicPlatformSearchIntegrationTests(CasazenWebApplicationFactory factory)
    {
        _factory = factory;
        FakeStripeService.Reset();
    }

    [Fact]
    public async Task PlatformStays_ListsTheFreeHousesOfEverySiteWithTheTotalOfTheirQuote_AndNothingThatIsHiddenOrCannotBeBooked()
    {
        var city = $"Piattaforma{Guid.NewGuid():N}";
        var friday = NextFriday(45);
        var monday = friday.AddDays(3);

        var orgA = await SeedOrgAsync("Host A", active: true, canTakeBookings: true);
        var orgB = await SeedOrgAsync("Host B", active: true, canTakeBookings: true);
        var inactive = await SeedOrgAsync("Host fermo", active: false, canTakeBookings: true);
        var noPayments = await SeedOrgAsync("Host senza conto", active: true, canTakeBookings: false);

        var a = await SeedHouseAsync(orgA.Id, "Casa A", city, p =>
        {
            p.Latitude = 45.64123m;
            p.Longitude = 9.13456m;
            p.WeekendSurchargePercent = 15m;
        });
        var b = await SeedHouseAsync(orgB.Id, "Casa B", city);
        var taken = await SeedHouseAsync(orgB.Id, "Casa occupata", city);
        await SeedHouseAsync(orgA.Id, "Casa nascosta", city, p => p.PublishedOnSite = false);
        await SeedHouseAsync(inactive.Id, "Casa di un host fermo", city);
        await SeedHouseAsync(noPayments.Id, "Casa che non si prenota", city);

        using var client = _factory.CreateClient();
        var held = await PostBookingAsync(client, BookingPayload(taken, friday, friday.AddDays(2)));
        Assert.Equal(HttpStatusCode.OK, held.StatusCode);

        var result = await GetStaysAsync(client, $"where={city}&checkin={Day(friday)}&checkout={Day(monday)}&adults=2");

        var items = result.GetProperty("items").EnumerateArray().ToList();
        Assert.Equal(new[] { a, b }.Order(), items.Select(i => i.GetProperty("id").GetGuid()).Order());
        Assert.Equal(3, result.GetProperty("totalCount").GetInt32());
        Assert.Equal(2, result.GetProperty("availableCount").GetInt32());
        Assert.All(items, i => Assert.True(i.GetProperty("availability").GetProperty("available").GetBoolean()));

        var cardA = items.First(i => i.GetProperty("id").GetGuid() == a);
        var cardB = items.First(i => i.GetProperty("id").GetGuid() == b);
        Assert.Equal(orgA.Slug, cardA.GetProperty("orgSlug").GetString());
        Assert.Equal("Host A", cardA.GetProperty("hostName").GetString());
        Assert.Equal(orgB.Slug, cardB.GetProperty("orgSlug").GetString());

        foreach (var (card, id) in new[] { (cardA, a), (cardB, b) })
        {
            var quote = await ReadAsync(await PostQuoteAsync(client, id, friday, monday, adults: 2));
            Assert.Equal(CentsOf(quote.GetProperty("totalPrice").GetDecimal()), card.GetProperty("price").GetProperty("totalCents").GetInt64());
        }

        Assert.Equal(45.64m, cardA.GetProperty("latitude").GetDecimal());
        Assert.Equal(9.13m, cardA.GetProperty("longitude").GetDecimal());

        var facets = result.GetProperty("facets");
        Assert.Equal(4, facets.GetProperty("maxGuests").GetInt32());
        Assert.Equal(CentsOf(150m), facets.GetProperty("minNightCents").GetInt64());
        Assert.Equal("EUR", facets.GetProperty("currency").GetString());
    }

    [Fact]
    public async Task PlatformStays_AHouseInSwitzerlandIsFoundByItsCountry_AndItsPriceIsInItsOwnCurrency()
    {
        var city = $"Lugano{Guid.NewGuid():N}";
        var friday = NextFriday(50);
        var monday = friday.AddDays(3);
        var org = await SeedOrgAsync("Host Ticino", active: true, canTakeBookings: true);
        var swiss = await SeedHouseAsync(org.Id, "Casa Ticino", city, p =>
        {
            p.Country = "CH";
            p.Currency = "CHF";
            p.NightlyRate = 200m;
        });
        using var client = _factory.CreateClient();

        foreach (var where in new[] { "svizzera", "Switzerland", city })
        {
            var result = await GetStaysAsync(client, $"where={where}&checkin={Day(friday)}&checkout={Day(monday)}&adults=2");
            var card = result.GetProperty("items").EnumerateArray().First(i => i.GetProperty("id").GetGuid() == swiss);
            Assert.Equal("CHF", card.GetProperty("price").GetProperty("currency").GetString());
            Assert.Equal("CH", card.GetProperty("country").GetString());
            Assert.All(result.GetProperty("items").EnumerateArray(), i => Assert.Equal("CH", i.GetProperty("country").GetString()));
        }

        var facets = (await GetStaysAsync(client, $"where={city}")).GetProperty("facets");
        Assert.Equal("CHF", facets.GetProperty("currency").GetString());
    }

    private static string Day(DateTime date) => date.ToString("yyyy-MM-dd");

    private static long CentsOf(decimal euros) => (long)Math.Round(euros * 100m, MidpointRounding.AwayFromZero);

    private static async Task<JsonElement> GetStaysAsync(HttpClient client, string query)
    {
        var response = await client.GetAsync($"/api/public/stays?{query}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await ReadAsync(response);
    }

    private static async Task<JsonElement> ReadAsync(HttpResponseMessage response)
    {
        var json = await response.Content.ReadAsStringAsync();
        return JsonDocument.Parse(json).RootElement.Clone();
    }

    private static Task<HttpResponseMessage> PostQuoteAsync(HttpClient client, Guid propertyId, DateTime checkIn, DateTime checkOut, int adults) =>
        client.PostAsJsonAsync("/api/public/bookings/quote", new
        {
            propertyId,
            checkInDate = Day(checkIn),
            checkOutDate = Day(checkOut),
            numberOfAdults = adults,
            numberOfChildren = 0,
            childrenAges = Array.Empty<int>(),
        });

    private static Task<HttpResponseMessage> PostBookingAsync(HttpClient client, object payload) =>
        client.PostAsJsonAsync("/api/public/bookings", payload);

    private static object BookingPayload(Guid propertyId, DateTime checkIn, DateTime checkOut) => new
    {
        propertyId,
        checkInDate = Day(checkIn),
        checkOutDate = Day(checkOut),
        numberOfAdults = 2,
        numberOfChildren = 0,
        guest = new
        {
            firstName = "Mario",
            lastName = "Rossi",
            email = $"mario.{Guid.NewGuid():N}@example.com",
            phone = "+393331234567",
            country = "IT",
        },
        consent = new { dataProcessing = true, consentVersion = ConsentVersion },
        paymentOption = "Immediate",
    };

    private static DateTime NextFriday(int minDaysAhead)
    {
        var day = TimeProvider.System.TodayInRome().AddDays(minDaysAhead);
        while (day.DayOfWeek != DayOfWeek.Friday)
            day = day.AddDays(1);

        return day;
    }

    private async Task<OrgEntity> SeedOrgAsync(string displayName, bool active, bool canTakeBookings)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var org = new OrgEntity
        {
            Name = displayName,
            Slug = $"platform-{Guid.NewGuid():N}",
            DisplayName = displayName,
            ContactEmail = "platform-search@example.com",
            PlanTier = PlanTier.Starter,
            IsActive = active,
            StripeConnectedAccountId = canTakeBookings ? "acct_test_connect_ready" : null,
            ConnectChargesEnabled = canTakeBookings,
        };
        db.Orgs.Add(org);
        await db.SaveChangesAsync();
        return org;
    }

    private async Task<Guid> SeedHouseAsync(Guid orgId, string name, string city, Action<Property>? configure = null)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var property = new Property
        {
            OwnerId = $"auth0|owner-{Guid.NewGuid():N}",
            OrgId = orgId,
            Name = name,
            Slug = $"casa-{Guid.NewGuid():N}",
            Description = "Integration test property",
            Address = $"Via Test {Guid.NewGuid():N}",
            City = city,
            PostalCode = "20822",
            Latitude = 45.64m,
            Longitude = 9.13m,
            Bedrooms = 2,
            Bathrooms = 1,
            MaxGuests = 4,
            NightlyRate = 150m,
            CleaningFee = 50m,
            CinCode = "IT108040C2ABCDEFGH",
            IsActive = true,
            ComplianceStatus = PropertyComplianceStatus.Active,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        configure?.Invoke(property);
        db.Properties.Add(property);
        await db.SaveChangesAsync();
        return property.Id;
    }
}
