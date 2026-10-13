using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Utilities;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using PlanTierEnum = Casazen.Core.Entities.Enums.PlanTier;

namespace Casazen.Tests.Integration;

/// <summary>
/// DB-03 over HTTP: the quote of the public booking site with its breakdown, the minimum stay, the weekend surcharge, the
/// public data of the org and of the property, and the promise that the exact address of a house never leaves CasaZen
/// through an anonymous endpoint. The comune of the properties (Seveso) has no tourist tax rate, so every total is the
/// price of the nights and of the cleaning: the tax is not what these tests are about (<c>TouristTaxQuoteIntegrationTests</c>).
/// </summary>
public class DirectBookingPublicDataIntegrationTests : IClassFixture<CasazenWebApplicationFactory>
{
    private const string ConsentVersion = "2026-06-direct-checkout-v1";

    // Distinctive values: if any of them shows up in an anonymous response, the test says which one.
    private const string SecretStreet = "Via Segretissima 17/B";
    private const string SecretUnit = "Scala C int. 9";
    private const decimal ExactLatitude = 45.464211m;
    private const decimal ExactLongitude = 9.191383m;

    private readonly CasazenWebApplicationFactory _factory;

    public DirectBookingPublicDataIntegrationTests(CasazenWebApplicationFactory factory)
    {
        _factory = factory;
        FakeStripeService.Reset();
    }

    // ─── Quote: nothing changes by default, and the breakdown adds up ───────────────────────────────

    [Fact]
    public async Task Quote_PropertyWithoutStayRules_KeepsEveryFieldOfTodayAndAddsTheBreakdown()
    {
        var (_, property) = await SeedAsync();
        using var client = _factory.CreateClient();
        var checkIn = NextFriday(40);

        var response = await PostQuoteAsync(client, property.Id, checkIn, checkIn.AddDays(3));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var quote = await ReadAsync(response);
        // The fields the frontend reads today, with the values of today: 3 nights x 150 + 50 of cleaning.
        Assert.Equal(property.Id, quote.GetProperty("propertyId").GetGuid());
        Assert.Equal(3, quote.GetProperty("nights").GetInt32());
        Assert.Equal(150m, quote.GetProperty("nightlyRate").GetDecimal());
        Assert.Equal(450m, quote.GetProperty("lodgingTotal").GetDecimal());
        Assert.Equal(50m, quote.GetProperty("cleaningFee").GetDecimal());
        Assert.Equal(500m, quote.GetProperty("basePrice").GetDecimal());
        Assert.Equal(500m, quote.GetProperty("totalPrice").GetDecimal());
        Assert.Equal("EUR", quote.GetProperty("currency").GetString());
        Assert.Equal("RateUnavailable", quote.GetProperty("touristTax").GetProperty("status").GetString());
        Assert.True(quote.TryGetProperty("paymentOptions", out _));
        Assert.True(quote.TryGetProperty("checkInDate", out _));

        // The breakdown, in cents: one line of nights (Friday and Saturday included: no surcharge), cleaning, total.
        var lines = quote.GetProperty("lines").EnumerateArray().ToList();
        Assert.Equal(["Nights", "CleaningFee", "Total"], lines.Select(l => l.GetProperty("kind").GetString()));
        Assert.Equal(3, lines[0].GetProperty("quantity").GetInt32());
        Assert.Equal(15000, lines[0].GetProperty("unitAmountCents").GetInt64());
        Assert.Equal(45000, lines[0].GetProperty("amountCents").GetInt64());
        Assert.Equal(5000, lines[1].GetProperty("amountCents").GetInt64());
        Assert.Equal(50000, lines[2].GetProperty("amountCents").GetInt64());
        Assert.Equal(JsonValueKind.Null, lines[2].GetProperty("quantity").ValueKind);
    }

    [Fact]
    public async Task Quote_WeekendSurcharge_SplitsTheNightsAndTheLinesAddUpToTheTotal()
    {
        var (_, property) = await SeedAsync(p => p.WeekendSurchargePercent = 15m);
        using var client = _factory.CreateClient();
        var checkIn = NextFriday(40);

        var quote = await ReadAsync(await PostQuoteAsync(client, property.Id, checkIn, checkIn.AddDays(3)));

        // Friday and Saturday at 150 + 15 % = 172.50, Sunday at 150, plus 50 of cleaning: 545.
        Assert.Equal(495m, quote.GetProperty("lodgingTotal").GetDecimal());
        Assert.Equal(545m, quote.GetProperty("basePrice").GetDecimal());
        Assert.Equal(545m, quote.GetProperty("totalPrice").GetDecimal());
        var lines = quote.GetProperty("lines").EnumerateArray().ToList();
        Assert.Equal(["Nights", "WeekendNights", "CleaningFee", "Total"], lines.Select(l => l.GetProperty("kind").GetString()));
        Assert.Equal((1, 15000L, 15000L), Triple(lines[0]));
        Assert.Equal((2, 17250L, 34500L), Triple(lines[1]));
        var total = lines[^1].GetProperty("amountCents").GetInt64();
        Assert.Equal(54500, total);
        Assert.Equal(total, lines.Take(lines.Count - 1).Sum(l => l.GetProperty("amountCents").GetInt64()));
    }

    [Fact]
    public async Task Quote_WeekendSurchargeButAWeekStay_HasNoWeekendLineAndTheOldTotal()
    {
        var (_, property) = await SeedAsync(p => p.WeekendSurchargePercent = 15m);
        using var client = _factory.CreateClient();
        var monday = NextFriday(40).AddDays(3);

        var quote = await ReadAsync(await PostQuoteAsync(client, property.Id, monday, monday.AddDays(3)));

        Assert.Equal(500m, quote.GetProperty("totalPrice").GetDecimal());
        Assert.DoesNotContain(quote.GetProperty("lines").EnumerateArray(), l => l.GetProperty("kind").GetString() == "WeekendNights");
    }

    [Fact]
    public async Task Quote_PriceTheHostConfirmedForAWeekendNight_IsNotRaisedByTheSurchargeAndHasItsOwnLine()
    {
        var (org, property) = await SeedAsync(p => p.WeekendSurchargePercent = 15m);
        using var client = _factory.CreateClient();
        var checkIn = NextFriday(40);
        // The host confirmed 200 for the Friday (PC-15); the Saturday has no price of its own, the Sunday is an ordinary night.
        await SeedConfirmedPriceAsync(org.Id, property.Id, DateOnly.FromDateTime(checkIn), 200m);

        var quote = await ReadAsync(await PostQuoteAsync(client, property.Id, checkIn, checkIn.AddDays(3)));

        // Friday 200 (the host's price, no surcharge on top) + Saturday 172.50 + Sunday 150 + cleaning 50.
        Assert.Equal(522.50m, quote.GetProperty("lodgingTotal").GetDecimal());
        Assert.Equal(572.50m, quote.GetProperty("totalPrice").GetDecimal());
        var lines = quote.GetProperty("lines").EnumerateArray().ToList();
        Assert.Equal(["Nights", "Nights", "WeekendNights", "CleaningFee", "Total"], lines.Select(l => l.GetProperty("kind").GetString()));
        Assert.Equal((1, 15000L, 15000L), Triple(lines[0]));
        Assert.Equal((1, 20000L, 20000L), Triple(lines[1]));
        Assert.Equal((1, 17250L, 17250L), Triple(lines[2]));
        Assert.Equal(57250L, lines[^1].GetProperty("amountCents").GetInt64());
        Assert.Equal(57250L, lines.Take(lines.Count - 1).Sum(l => l.GetProperty("amountCents").GetInt64()));
    }

    [Fact]
    public async Task BookingAfterTheQuote_RecordsAndChargesTheQuotedTotalWithTheWeekendSurcharge()
    {
        var (_, property) = await SeedAsync(p => p.WeekendSurchargePercent = 15m);
        using var client = _factory.CreateClient();
        var checkIn = NextFriday(40);
        var quote = await ReadAsync(await PostQuoteAsync(client, property.Id, checkIn, checkIn.AddDays(3)));

        var response = await PostBookingAsync(client, BookingPayload(property.Id, checkIn, checkIn.AddDays(3)));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var booked = await ReadAsync(response);
        Assert.Equal(quote.GetProperty("totalPrice").GetDecimal(), booked.GetProperty("amount").GetDecimal());
        var bookingId = booked.GetProperty("bookingId").GetGuid();
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var stored = await db.Bookings.IgnoreQueryFilters().AsNoTracking().SingleAsync(b => b.Id == bookingId);
        Assert.Equal(545m, stored.TotalPrice);
        Assert.Equal(545m, stored.BasePrice);
        var payment = await db.Payments.IgnoreQueryFilters().AsNoTracking().SingleAsync(p => p.BookingId == bookingId);
        Assert.Equal(545m, payment.Amount);
    }

    // ─── Minimum stay ───────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Quote_StayShorterThanTheMinimum_Returns422WithTheStableCodeAndTheMinimumInBothLanguages()
    {
        var (_, property) = await SeedAsync(p => p.MinNights = 3);
        var checkIn = NextFriday(40);

        using var italian = _factory.CreateClient();
        var it = await PostQuoteAsync(italian, property.Id, checkIn, checkIn.AddDays(2));
        using var english = _factory.CreateClient();
        english.DefaultRequestHeaders.AcceptLanguage.ParseAdd("en");
        var en = await PostQuoteAsync(english, property.Id, checkIn, checkIn.AddDays(2));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, it.StatusCode);
        var itProblem = await ReadAsync(it);
        Assert.Equal("direct_booking_min_nights_not_met", itProblem.GetProperty("code").GetString());
        Assert.Contains("3 notti", itProblem.GetProperty("detail").GetString());
        Assert.Equal(HttpStatusCode.UnprocessableEntity, en.StatusCode);
        var enProblem = await ReadAsync(en);
        Assert.Equal("direct_booking_min_nights_not_met", enProblem.GetProperty("code").GetString());
        Assert.Contains("3 nights", enProblem.GetProperty("detail").GetString());
    }

    [Theory]
    [InlineData(3)]
    [InlineData(5)]
    public async Task Quote_StayAtOrAboveTheMinimum_IsPriced(int nights)
    {
        var (_, property) = await SeedAsync(p => p.MinNights = 3);
        using var client = _factory.CreateClient();
        var checkIn = NextFriday(40).AddDays(3);

        var response = await PostQuoteAsync(client, property.Id, checkIn, checkIn.AddDays(nights));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(150m * nights + 50m, (await ReadAsync(response)).GetProperty("totalPrice").GetDecimal());
    }

    [Fact]
    public async Task Booking_StayShorterThanTheMinimum_Returns422AndStoresNothing()
    {
        var (_, property) = await SeedAsync(p => p.MinNights = 4);
        using var client = _factory.CreateClient();
        var checkIn = NextFriday(40);

        var response = await PostBookingAsync(client, BookingPayload(property.Id, checkIn, checkIn.AddDays(3)));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var problem = await ReadAsync(response);
        Assert.Equal("direct_booking_min_nights_not_met", problem.GetProperty("code").GetString());
        Assert.False(string.IsNullOrWhiteSpace(problem.GetProperty("detail").GetString()));
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.False(await db.Bookings.IgnoreQueryFilters().AnyAsync(b => b.PropertyId == property.Id));
        Assert.False(await db.Guests.IgnoreQueryFilters().AnyAsync(g => g.OrgId == property.OrgId));
    }

    [Fact]
    public async Task Booking_StayAtTheMinimum_IsAccepted()
    {
        var (_, property) = await SeedAsync(p => p.MinNights = 3);
        using var client = _factory.CreateClient();
        var checkIn = NextFriday(40);

        var response = await PostBookingAsync(client, BookingPayload(property.Id, checkIn, checkIn.AddDays(3)));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Availability_ExposesTheMinimumStayOfTheProperty()
    {
        var (_, withMinimum) = await SeedAsync(p => p.MinNights = 2);
        var (_, withoutMinimum) = await SeedAsync();
        using var client = _factory.CreateClient();

        var with = await client.GetFromJsonAsync<JsonElement>($"/api/public/bookings/property/{withMinimum.Id}/availability");
        var without = await client.GetFromJsonAsync<JsonElement>($"/api/public/bookings/property/{withoutMinimum.Id}/availability");

        Assert.Equal(2, with.GetProperty("minNights").GetInt32());
        Assert.Equal(JsonValueKind.Null, without.GetProperty("minNights").ValueKind);
        // What the endpoint always answered is still there.
        Assert.True(with.TryGetProperty("bookedDates", out _));
    }

    // ─── Public data of the org and of the property ─────────────────────────────────────────────────

    [Fact]
    public async Task PublicProperty_DetailCarriesTheStayRulesAndTheHostDataOnBothRoutes()
    {
        var (org, property) = await SeedAsync(
            p =>
            {
                p.MinNights = 2;
                p.WeekendSurchargePercent = 12.5m;
            },
            o =>
            {
                o.HostName = "Giulia";
                o.PublicPhone = "+393331234567";
            });
        using var client = _factory.CreateClient();

        var byOrg = await client.GetFromJsonAsync<JsonElement>($"/api/public/orgs/{org.Slug}/properties/{property.Slug}");
        var byId = await client.GetFromJsonAsync<JsonElement>($"/api/properties/{property.Id}/public");

        foreach (var detail in new[] { byOrg, byId })
        {
            Assert.Equal(2, detail.GetProperty("minNights").GetInt32());
            Assert.Equal(12.5m, detail.GetProperty("weekendSurchargePercent").GetDecimal());
            Assert.True(detail.GetProperty("acceptsBookings").GetBoolean());
            Assert.Equal("Giulia", detail.GetProperty("hostName").GetString());
            Assert.Equal("+393331234567", detail.GetProperty("publicPhone").GetString());
        }
    }

    [Fact]
    public async Task PublicProperty_DefaultsOfTodaysProperties_AreNoMinimumNoSurchargeAndNoHostData()
    {
        var (org, property) = await SeedAsync();
        using var client = _factory.CreateClient();

        var detail = await client.GetFromJsonAsync<JsonElement>($"/api/public/orgs/{org.Slug}/properties/{property.Slug}");

        Assert.Equal(JsonValueKind.Null, detail.GetProperty("minNights").ValueKind);
        Assert.Equal(0m, detail.GetProperty("weekendSurchargePercent").GetDecimal());
        Assert.Equal(JsonValueKind.Null, detail.GetProperty("hostName").ValueKind);
        Assert.Equal(JsonValueKind.Null, detail.GetProperty("publicPhone").ValueKind);
    }

    [Fact]
    public async Task PublicOrg_AcceptsBookings_IsTrueExactlyWhenTheCheckoutTakesTheBooking()
    {
        var (readyOrg, ready) = await SeedAsync();
        var (blockedOrg, blocked) = await SeedAsync(connect: false);
        using var client = _factory.CreateClient();
        var checkIn = NextFriday(40);

        var readyDto = await client.GetFromJsonAsync<JsonElement>($"/api/public/orgs/{readyOrg.Slug}");
        var blockedDto = await client.GetFromJsonAsync<JsonElement>($"/api/public/orgs/{blockedOrg.Slug}");
        var bookReady = await PostBookingAsync(client, BookingPayload(ready.Id, checkIn, checkIn.AddDays(3)));
        var bookBlocked = await PostBookingAsync(client, BookingPayload(blocked.Id, checkIn, checkIn.AddDays(3)));

        Assert.True(readyDto.GetProperty("acceptsBookings").GetBoolean());
        Assert.False(blockedDto.GetProperty("acceptsBookings").GetBoolean());
        Assert.Equal(HttpStatusCode.OK, bookReady.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, bookBlocked.StatusCode);
        Assert.Equal("direct_booking_payments_not_ready", (await ReadAsync(bookBlocked)).GetProperty("code").GetString());
    }

    [Fact]
    public async Task PublicOrg_WithNothingPublished_ExposesNoPhoneNoHostNameNoSubtitleAndNoMarketingVersion()
    {
        var (org, _) = await SeedAsync();
        using var client = _factory.CreateClient();

        var dto = await client.GetFromJsonAsync<JsonElement>($"/api/public/orgs/{org.Slug}");

        Assert.Equal(JsonValueKind.Null, dto.GetProperty("subtitle").ValueKind);
        Assert.Equal(JsonValueKind.Null, dto.GetProperty("hostName").ValueKind);
        Assert.Equal(JsonValueKind.Null, dto.GetProperty("publicPhone").ValueKind);
        // The test host has no Gdpr:MarketingConsentVersion: the box is not offered.
        Assert.Equal(JsonValueKind.Null, dto.GetProperty("marketingConsentVersion").ValueKind);
        // The private contact email of the org stays behind its own opt-in.
        Assert.Equal(JsonValueKind.Null, dto.GetProperty("contactEmail").ValueKind);
    }

    // ─── The exact address never leaves ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task AnonymousEndpoints_NeverReturnTheExactAddressTheUnitOrTheExactCoordinates()
    {
        var (org, property) = await SeedAsync(
            p =>
            {
                p.Address = SecretStreet;
                p.Unit = SecretUnit;
                p.Latitude = ExactLatitude;
                p.Longitude = ExactLongitude;
                p.City = "Seveso";
            });
        using var client = _factory.CreateClient();
        var checkIn = NextFriday(40);
        var quoteBody = JsonContent.Create(new
        {
            propertyId = property.Id,
            checkInDate = checkIn.ToString("yyyy-MM-dd"),
            checkOutDate = checkIn.AddDays(2).ToString("yyyy-MM-dd"),
            numberOfAdults = 2,
            numberOfChildren = 0,
        });

        var anonymousGets = new[]
        {
            $"/api/public/orgs/{org.Slug}",
            $"/api/public/orgs/{org.Slug}/properties",
            $"/api/public/orgs/{org.Slug}/properties/{property.Slug}",
            $"/api/public/orgs/{org.Slug}/properties/{property.Id}",
            "/api/properties/search?city=Seveso",
            $"/api/properties/{property.Id}/public",
            $"/api/public/seo/orgs/{org.Slug}",
            $"/api/public/seo/orgs/{org.Slug}/properties/{property.Slug}",
            $"/api/public/bookings/property/{property.Id}/availability",
            $"/api/public/orgs/{org.Slug}/sitemap.xml",
            "/api/public/sitemap-book.xml",
        };

        var bodies = new List<(string Url, string Body)>();
        foreach (var url in anonymousGets)
        {
            var response = await client.GetAsync(url);
            bodies.Add((url, await response.Content.ReadAsStringAsync()));
        }

        var quote = await client.PostAsync("/api/public/bookings/quote", quoteBody);
        bodies.Add(("POST /api/public/bookings/quote", await quote.Content.ReadAsStringAsync()));

        // The endpoints that give the property out are really answering (a 404 would pass the checks below for nothing).
        Assert.Contains(bodies, b => b.Url.EndsWith($"/properties/{property.Slug}", StringComparison.Ordinal) && b.Body.Contains(property.Name));
        Assert.Contains(bodies, b => b.Url == $"/api/properties/{property.Id}/public" && b.Body.Contains(property.Name));

        foreach (var (url, body) in bodies)
        {
            foreach (var secret in new[] { "Segretissima", "Scala C", "17/B", "45.464211", "9.191383", "45.4642", "9.1913", "45.46421", "9.19138" })
                Assert.False(body.Contains(secret, StringComparison.OrdinalIgnoreCase), $"{url} leaks '{secret}'");
        }
    }

    [Fact]
    public async Task PublicProperty_Coordinates_AreRoundedToTwoDecimalsOnEveryRouteThatGivesThemOut()
    {
        var (org, property) = await SeedAsync(
            p =>
            {
                p.Latitude = ExactLatitude;
                p.Longitude = ExactLongitude;
                p.City = "Seveso";
            });
        using var client = _factory.CreateClient();

        var list = await client.GetFromJsonAsync<JsonElement>($"/api/public/orgs/{org.Slug}/properties");
        var detail = await client.GetFromJsonAsync<JsonElement>($"/api/public/orgs/{org.Slug}/properties/{property.Slug}");
        var byId = await client.GetFromJsonAsync<JsonElement>($"/api/properties/{property.Id}/public");
        var search = await client.GetFromJsonAsync<JsonElement>("/api/properties/search?city=Seveso");

        var shown = new[] { list[0], detail, byId, search.EnumerateArray().First(p => p.GetProperty("id").GetGuid() == property.Id) };
        foreach (var dto in shown)
        {
            Assert.Equal(45.46m, dto.GetProperty("latitude").GetDecimal());
            Assert.Equal(9.19m, dto.GetProperty("longitude").GetDecimal());
        }

        // The host reads the exact position back from the stored record, unchanged.
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var stored = await db.Properties.IgnoreQueryFilters().AsNoTracking().SingleAsync(p => p.Id == property.Id);
        Assert.Equal(ExactLatitude, stored.Latitude);
        Assert.Equal(ExactLongitude, stored.Longitude);
    }

    [Fact]
    public async Task CrawlerPage_JsonLdCarriesTheRoundedPositionOnly()
    {
        var (org, property) = await SeedAsync(
            p =>
            {
                p.Latitude = ExactLatitude;
                p.Longitude = ExactLongitude;
            });
        using var client = _factory.CreateClient();

        var html = await client.GetStringAsync($"/api/public/seo/orgs/{org.Slug}/properties/{property.Slug}");

        Assert.Contains("\"latitude\":45.46", html);
        Assert.Contains("\"longitude\":9.19", html);
        Assert.DoesNotContain("45.464211", html);
        Assert.DoesNotContain("9.191383", html);
        Assert.DoesNotContain("Segretissima", html);
    }

    // ─── Helpers ────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The first Friday that is at least <paramref name="minDaysAhead"/> days from today in Rome (midnight UTC of its date).</summary>
    private static DateTime NextFriday(int minDaysAhead)
    {
        var day = TimeProvider.System.TodayInRome().AddDays(minDaysAhead);
        while (day.DayOfWeek != DayOfWeek.Friday)
            day = day.AddDays(1);

        return day;
    }

    /// <summary>A seasonal price that the host confirmed for one date of the property (what the pricing adapter stores, PC-15).</summary>
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

    private static (int Quantity, long Unit, long Amount) Triple(JsonElement line) =>
        (line.GetProperty("quantity").GetInt32(), line.GetProperty("unitAmountCents").GetInt64(), line.GetProperty("amountCents").GetInt64());

    private static Task<HttpResponseMessage> PostQuoteAsync(HttpClient client, Guid propertyId, DateTime checkIn, DateTime checkOut) =>
        client.PostAsync(
            "/api/public/bookings/quote",
            new StringContent(
                JsonSerializer.Serialize(new
                {
                    propertyId,
                    checkInDate = checkIn.ToString("yyyy-MM-dd"),
                    checkOutDate = checkOut.ToString("yyyy-MM-dd"),
                    numberOfAdults = 2,
                    numberOfChildren = 0,
                }),
                Encoding.UTF8,
                "application/json"));

    private static Task<HttpResponseMessage> PostBookingAsync(HttpClient client, object payload) =>
        client.PostAsync("/api/public/bookings", new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"));

    private static object BookingPayload(Guid propertyId, DateTime checkIn, DateTime checkOut) => new
    {
        propertyId,
        checkInDate = checkIn.ToString("yyyy-MM-dd"),
        checkOutDate = checkOut.ToString("yyyy-MM-dd"),
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

    private static async Task<JsonElement> ReadAsync(HttpResponseMessage response)
    {
        var json = await response.Content.ReadAsStringAsync();
        return JsonDocument.Parse(json).RootElement.Clone();
    }

    /// <summary>An org with a property published in Seveso (no tourist tax rate), by default with a ready Stripe Connect account.</summary>
    private async Task<(OrgEntity Org, Property Property)> SeedAsync(
        Action<Property>? property = null,
        Action<OrgEntity>? org = null,
        bool connect = true)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var seededOrg = new OrgEntity
        {
            Name = "DB-03 Org",
            Slug = $"db03-{Guid.NewGuid():N}",
            DisplayName = "DB-03 Org",
            ContactEmail = "db03@example.com",
            PlanTier = PlanTierEnum.Starter,
            IsActive = true,
            StripeConnectedAccountId = connect ? "acct_test_connect_ready" : null,
            ConnectChargesEnabled = connect,
        };
        org?.Invoke(seededOrg);
        db.Orgs.Add(seededOrg);

        var seededProperty = new Property
        {
            OwnerId = $"auth0|owner-{Guid.NewGuid():N}",
            OrgId = seededOrg.Id,
            Name = "Casa DB-03",
            Slug = $"casa-{Guid.NewGuid():N}",
            Description = "Integration test property",
            Address = $"Via Test {Guid.NewGuid():N}",
            City = "Seveso",
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
        property?.Invoke(seededProperty);
        db.Properties.Add(seededProperty);
        await db.SaveChangesAsync();
        return (seededOrg, seededProperty);
    }
}
