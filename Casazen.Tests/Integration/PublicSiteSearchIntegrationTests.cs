using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Enums;
using Casazen.Core.SiteProfile;
using Casazen.Core.Utilities;
using Casazen.Infrastructure.Data;
using Casazen.Tests.Unit.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Casazen.Tests.Integration;

/// <summary>
/// DB-07a over HTTP: the search of a booking site gives, for every house of that site (and of no other), the total of the stay that the quote
/// of the checkout gives for the same dates and party, to the cent, with the tourist tax (exact for adults, estimated while the ages of the
/// children are unknown); a house that is taken stays in the list with the reason and the first free day.
/// </summary>
public class PublicSiteSearchIntegrationTests : IClassFixture<CasazenWebApplicationFactory>
{
    private const string ConsentVersion = "2026-06-direct-checkout-v1";

    private readonly CasazenWebApplicationFactory _factory;

    public PublicSiteSearchIntegrationTests(CasazenWebApplicationFactory factory)
    {
        _factory = factory;
        FakeStripeService.Reset();
    }

    [Fact]
    public async Task Stays_TheTotalOfEveryHouseIsTheTotalOfItsQuote_ATakenHouseSaysWhy_AndNoHouseOfAnotherOrgAppears()
    {
        // A comune with a tourist tax (1.50 per person per night, from 14 years old) that no other test uses.
        var city = $"Tassa{Guid.NewGuid():N}";
        await SeedTaxRateAsync(city);
        var (org, alpha) = await SeedOrgWithHouseAsync("Casa Alfa", city, p => p.WeekendSurchargePercent = 15m);
        var beta = await SeedHouseAsync(org.Id, "Casa Beta", city);
        var (_, stranger) = await SeedOrgWithHouseAsync("Casa Altrui", city);
        var friday = NextFriday(40);
        var monday = friday.AddDays(3);
        // The host confirmed 200 for the Friday of the first house; the second house is held by a guest who is paying (Friday and Saturday).
        await SeedConfirmedPriceAsync(org.Id, alpha, DateOnly.FromDateTime(friday), 200m);
        using var client = _factory.CreateClient();
        var held = await PostBookingAsync(client, BookingPayload(beta, friday, friday.AddDays(2)));
        Assert.Equal(HttpStatusCode.OK, held.StatusCode);

        var adults = await GetStaysAsync(client, org.Slug, $"checkin={Day(friday)}&checkout={Day(monday)}&adults=2");
        var withChild = await GetStaysAsync(client, org.Slug, $"checkin={Day(friday)}&checkout={Day(monday)}&adults=2&children=1");

        // Only the two houses of this site, the free one first; the stranger's house is nowhere.
        var items = adults.GetProperty("items").EnumerateArray().ToList();
        Assert.Equal(2, adults.GetProperty("totalCount").GetInt32());
        Assert.Equal([alpha, beta], items.Select(i => i.GetProperty("id").GetGuid()));
        Assert.DoesNotContain(stranger, items.Select(i => i.GetProperty("id").GetGuid()));

        // The free house: the total is the one of the quote, with the tax worked out for this party.
        var quote = await ReadAsync(await PostQuoteAsync(client, alpha, friday, monday, adults: 2, childrenAges: []));
        Assert.True(items[0].GetProperty("availability").GetProperty("available").GetBoolean());
        Assert.Equal(CentsOf(quote.GetProperty("totalPrice").GetDecimal()), items[0].GetProperty("price").GetProperty("totalCents").GetInt64());
        Assert.Equal("exact", items[0].GetProperty("price").GetProperty("includesTouristTax").GetString());
        Assert.Equal(CentsOf(quote.GetProperty("lodgingTotal").GetDecimal() / 3), items[0].GetProperty("price").GetProperty("perNightCents").GetInt64());

        // With a child of unknown age the list counts them as a payer: the quote of a child of 17 gives the same total, and the list says estimated.
        var childItem = withChild.GetProperty("items").EnumerateArray().First(i => i.GetProperty("id").GetGuid() == alpha);
        var quoteWithChild = await ReadAsync(await PostQuoteAsync(client, alpha, friday, monday, adults: 2, childrenAges: [17]));
        Assert.Equal(
            CentsOf(quoteWithChild.GetProperty("totalPrice").GetDecimal()),
            childItem.GetProperty("price").GetProperty("totalCents").GetInt64());
        Assert.Equal("estimated", childItem.GetProperty("price").GetProperty("includesTouristTax").GetString());

        // The taken house stays in the list, says why and says when a stay of those three nights can start.
        var taken = items[1].GetProperty("availability");
        Assert.False(taken.GetProperty("available").GetBoolean());
        Assert.Equal("busy", taken.GetProperty("reason").GetString());
        Assert.Equal(Day(friday.AddDays(2)), taken.GetProperty("firstFreeCheckIn").GetString());
        Assert.Equal(1, adults.GetProperty("availableCount").GetInt32());
        Assert.Equal(0, adults.GetProperty("alternatives").GetArrayLength());
    }

    [Fact]
    public async Task Stays_AHouseWhoseHostGaveNoPositionIsAtTheCentreOfItsComune_AndOneThatGaveItKeepsIt()
    {
        var city = $"Posizione{Guid.NewGuid():N}";
        // Without a position (0, 0 = "not set") but with the comune chosen from the official list: Como, ISTAT 013075.
        var (org, withoutPosition) = await SeedOrgWithHouseAsync("Casa Senza Posizione", city, p =>
        {
            p.Latitude = 0m;
            p.Longitude = 0m;
            p.ComuneIstatCode = "013075";
        });
        // Without a position and without a comune: nothing to put it by.
        var withoutComune = await SeedHouseAsync(org.Id, "Casa Senza Comune", city, p =>
        {
            p.Latitude = 0m;
            p.Longitude = 0m;
        });
        // With the position the host gave: it stays theirs, to about a kilometre, whatever the comune says.
        var withPosition = await SeedHouseAsync(org.Id, "Casa Con Posizione", city, p =>
        {
            p.Latitude = 45.464211m;
            p.Longitude = 9.191383m;
            p.ComuneIstatCode = "013075";
        });
        using var client = _factory.CreateClient();

        var stays = await GetStaysAsync(client, org.Slug, "adults=2");

        var cards = stays.GetProperty("items").EnumerateArray().ToDictionary(i => i.GetProperty("id").GetGuid());
        Assert.Equal(3, cards.Count);
        // The centre of Como is 45.8103, 9.0861.
        Assert.Equal(45.81m, cards[withoutPosition].GetProperty("latitude").GetDecimal());
        Assert.Equal(9.09m, cards[withoutPosition].GetProperty("longitude").GetDecimal());
        Assert.Equal(0m, cards[withoutComune].GetProperty("latitude").GetDecimal());
        Assert.Equal(0m, cards[withoutComune].GetProperty("longitude").GetDecimal());
        Assert.Equal(45.46m, cards[withPosition].GetProperty("latitude").GetDecimal());
        Assert.Equal(9.19m, cards[withPosition].GetProperty("longitude").GetDecimal());
    }

    [Fact]
    public async Task Stays_TheLabelsAndTheChildrenRuleFilterTheList_AndTheCardSaysWhatCancellingCostsWithTheDatesOfTheQuote()
    {
        var city = $"Etichette{Guid.NewGuid():N}";
        // Alfa: a pool (a label its data give), a sea view the host chose, children welcome, the zone written, a cancellation policy;
        // Beta: the host said nothing.
        var (org, alfa) = await SeedOrgWithHouseAsync("Casa Alfa", city, p =>
        {
            p.Amenities = [PropertyAmenity.Pool];
            p.ChildrenWelcome = true;
            p.SiteProfileJson = SitePropertyProfileJson.Serialize(new SitePropertyProfileDocument
            {
                Zone = new LocalizedText("Centro storico", "Old town"),
                Tags = ["vista-mare"],
            });
            p.CancellationPolicy = CatalogPolicies.Get(CatalogPolicies.Contenuta);
        });
        var beta = await SeedHouseAsync(org.Id, "Casa Beta", city);
        using var client = _factory.CreateClient();
        var friday = NextFriday(60);
        var monday = friday.AddDays(3);
        var dates = $"checkin={Day(friday)}&checkout={Day(monday)}&adults=2";

        async Task<List<Guid>> IdsAsync(string query) =>
            (await GetStaysAsync(client, org.Slug, query)).GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetGuid()).ToList();

        // A label the data give, one the host chose, both, and the rule about children: only the house that has them.
        Assert.Equal([alfa], await IdsAsync("tags=piscina"));
        Assert.Equal([alfa], await IdsAsync("tags=vista-mare"));
        Assert.Equal([alfa], await IdsAsync("tags=piscina,vista-mare"));
        Assert.Equal([alfa], await IdsAsync("rules=children"));
        Assert.Equal(new[] { alfa, beta }.Order(), (await IdsAsync("")).Order());

        // A label that does not exist is left out and said so, never an error: the two houses stay.
        var unknown = await GetStaysAsync(client, org.Slug, "tags=con-sauna");
        Assert.Equal(2, unknown.GetProperty("totalCount").GetInt32());
        Assert.Equal(["tags:con-sauna"], unknown.GetProperty("unsupportedFilters").EnumerateArray().Select(f => f.GetString()));

        // The card: the zone as the host wrote it, the labels in the order of the vocabulary.
        var items = (await GetStaysAsync(client, org.Slug, dates)).GetProperty("items").EnumerateArray().ToList();
        var card = items.First(i => i.GetProperty("id").GetGuid() == alfa);
        Assert.Equal("Old town", card.GetProperty("zone").GetProperty("en").GetString());
        Assert.Equal(["piscina", "vista-mare"], card.GetProperty("tags").EnumerateArray().Select(t => t.GetString()));

        // What cancelling costs is what the quote of that stay says, date for date; a house with no policy says nothing.
        var quote = (await ReadAsync(await PostQuoteAsync(client, alfa, friday, monday, adults: 2, childrenAges: []))).GetProperty("cancellation");
        var onCard = card.GetProperty("cancellation");
        Assert.Equal(quote.GetProperty("fullRefundUntil").GetString(), onCard.GetProperty("fullRefundUntil").GetString());
        Assert.Equal(quote.GetProperty("partialRefundUntil").GetString(), onCard.GetProperty("partialRefundUntil").GetString());
        Assert.Equal(quote.GetProperty("partialRefundPercent").GetDecimal(), onCard.GetProperty("partialRefundPercent").GetDecimal());
        Assert.NotEqual(JsonValueKind.Null, onCard.GetProperty("fullRefundUntil").ValueKind);
        Assert.Equal(JsonValueKind.Null, items.First(i => i.GetProperty("id").GetGuid() == beta).GetProperty("cancellation").ValueKind);
    }

    private static string Day(DateTime date) => date.ToString("yyyy-MM-dd");

    private static long CentsOf(decimal euros) => (long)Math.Round(euros * 100m, MidpointRounding.AwayFromZero);

    private static async Task<JsonElement> GetStaysAsync(HttpClient client, string slug, string query)
    {
        var response = await client.GetAsync($"/api/public/orgs/{slug}/stays?{query}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await ReadAsync(response);
    }

    private static async Task<JsonElement> ReadAsync(HttpResponseMessage response)
    {
        var json = await response.Content.ReadAsStringAsync();
        return JsonDocument.Parse(json).RootElement.Clone();
    }

    private static Task<HttpResponseMessage> PostQuoteAsync(
        HttpClient client,
        Guid propertyId,
        DateTime checkIn,
        DateTime checkOut,
        int adults,
        int[] childrenAges) =>
        client.PostAsJsonAsync("/api/public/bookings/quote", new
        {
            propertyId,
            checkInDate = Day(checkIn),
            checkOutDate = Day(checkOut),
            numberOfAdults = adults,
            numberOfChildren = childrenAges.Length,
            childrenAges,
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

    /// <summary>The first Friday that is at least <paramref name="minDaysAhead"/> days from today in Rome (midnight UTC of its date).</summary>
    private static DateTime NextFriday(int minDaysAhead)
    {
        var day = TimeProvider.System.TodayInRome().AddDays(minDaysAhead);
        while (day.DayOfWeek != DayOfWeek.Friday)
            day = day.AddDays(1);

        return day;
    }

    private async Task SeedTaxRateAsync(string city)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.TouristTaxRates.Add(new TouristTaxRate
        {
            City = city,
            RegionCode = "LOM",
            RatePerPersonPerNight = 1.50m,
            MaxNights = 5,
            MinimumAge = 14,
            IsActive = true,
            EffectiveFrom = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        });
        await db.SaveChangesAsync();
    }

    /// <summary>A price the host confirmed for one date of a house (what the pricing adapter stores).</summary>
    private async Task SeedConfirmedPriceAsync(Guid orgId, Guid propertyId, DateOnly date, decimal price)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.SeasonalPriceSuggestions.Add(new SeasonalPriceSuggestion
        {
            OrgId = orgId,
            PropertyId = propertyId,
            StayDate = date,
            BasePrice = 150m,
            SuggestedPrice = price,
            Multiplier = 1.33m,
            Rule = Casazen.Core.Pricing.SeasonalPriceRule.HighSeason,
            ComputedAt = DateTime.UtcNow,
            AppliedPrice = price,
            AppliedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
    }

    private async Task<(OrgEntity Org, Guid PropertyId)> SeedOrgWithHouseAsync(string name, string city, Action<Property>? configure = null)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var org = new OrgEntity
        {
            Name = "DB-07a Org",
            Slug = $"db07a-{Guid.NewGuid():N}",
            DisplayName = "DB-07a Org",
            ContactEmail = "db07a@example.com",
            PlanTier = PlanTier.Starter,
            IsActive = true,
            StripeConnectedAccountId = "acct_test_connect_ready",
            ConnectChargesEnabled = true,
        };
        db.Orgs.Add(org);
        await db.SaveChangesAsync();
        var propertyId = await AddHouseAsync(db, org.Id, name, city, configure);
        return (org, propertyId);
    }

    private async Task<Guid> SeedHouseAsync(Guid orgId, string name, string city, Action<Property>? configure = null)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await AddHouseAsync(db, orgId, name, city, configure);
    }

    private static async Task<Guid> AddHouseAsync(AppDbContext db, Guid orgId, string name, string city, Action<Property>? configure)
    {
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
