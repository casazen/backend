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
/// PM-01 over the real pipeline: a property in long-term mode answers 404 on every public read (property page, org site,
/// search, availability, SEO page, sitemap), 422 <c>property_not_bookable_in_long_mode</c> (Italian and English) on the
/// public checkout, its quote, the host's manual booking and the activation, and is listed by <c>GET /api/properties</c>
/// with its mode (<c>?mode=short|long</c> narrows the list); a property created like the long-rent form always did is
/// <c>Long</c>, and the generic update never changes the mode. The same property in short-rent mode keeps answering as
/// before (the controls of each test).
/// </summary>
public class PropertyRentalModeIntegrationTests : IClassFixture<CasazenWebApplicationFactory>
{
    private const string ConsentVersion = "2026-06-direct-checkout-v1";
    private const string HostRole = "PropertyOwner";
    private const string StableCode = "property_not_bookable_in_long_mode";

    private readonly CasazenWebApplicationFactory _factory;

    public PropertyRentalModeIntegrationTests(CasazenWebApplicationFactory factory) => _factory = factory;

    // ─── Public reads: 404 ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task PublicReads_LongProperty_Answer404AndTheShortOneKeepsAnswering()
    {
        var host = await SeedHostAsync("reads");
        var shortStay = await SeedPropertyAsync(host, RentalMode.Short, "Casa Breve");
        var longTerm = await SeedPropertyAsync(host, RentalMode.Long, "Bilocale Lungo");
        using var anonymous = _factory.CreateClient();

        foreach (var path in PublicPaths(host, shortStay))
        {
            var response = await anonymous.GetAsync(path);
            Assert.True(response.StatusCode == HttpStatusCode.OK, $"{path} answered {(int)response.StatusCode}, expected 200 for the short-rent property");
        }

        foreach (var path in PublicPaths(host, longTerm))
        {
            var response = await anonymous.GetAsync(path);
            Assert.True(response.StatusCode == HttpStatusCode.NotFound, $"{path} answered {(int)response.StatusCode}, expected 404 for the long-term property");
        }
    }

    [Fact]
    public async Task PublicAvailability_LongProperty_Is404WithTheStableCode()
    {
        var host = await SeedHostAsync("availability");
        var longTerm = await SeedPropertyAsync(host, RentalMode.Long, "Bilocale Lungo");
        using var anonymous = _factory.CreateClient();

        var response = await anonymous.GetAsync(
            $"/api/public/bookings/property/{longTerm.Id}/availability?startDate={Day(10):yyyy-MM-dd}&endDate={Day(20):yyyy-MM-dd}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("public_property_not_found", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
    }

    [Fact]
    public async Task PublicLists_LongProperty_IsNotListedAnywhere()
    {
        var host = await SeedHostAsync("lists");
        var city = $"Citta{Guid.NewGuid():N}"[..20];
        var shortStay = await SeedPropertyAsync(host, RentalMode.Short, "Casa Breve", city);
        var longTerm = await SeedPropertyAsync(host, RentalMode.Long, "Bilocale Lungo", city);
        using var anonymous = _factory.CreateClient();

        var orgList = await anonymous.GetFromJsonAsync<JsonElement>($"/api/public/orgs/{host.OrgSlug}/properties");
        Assert.Equal([shortStay.Id], orgList.EnumerateArray().Select(p => p.GetProperty("id").GetGuid()).ToList());

        var search = await anonymous.GetFromJsonAsync<JsonElement>($"/api/properties/search?city={city}");
        Assert.Equal([shortStay.Id], search.EnumerateArray().Select(p => p.GetProperty("id").GetGuid()).ToList());

        var sitemap = await anonymous.GetStringAsync($"/api/public/orgs/{host.OrgSlug}/sitemap.xml");
        Assert.Contains(shortStay.Slug!, sitemap);
        Assert.DoesNotContain(longTerm.Slug!, sitemap);
        Assert.DoesNotContain(longTerm.Id.ToString(), sitemap);
    }

    // ─── Bookings: 422 ──────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("it-IT", "è in modalità affitto lungo")]
    [InlineData("en", "set to long-term rental")]
    public async Task PublicCheckoutAndQuote_LongProperty_Are422WithTheLocalizedReason(string language, string expectedText)
    {
        var host = await SeedHostAsync("checkout");
        var longTerm = await SeedPropertyAsync(host, RentalMode.Long, "Bilocale Lungo");
        using var anonymous = _factory.CreateClient();
        anonymous.DefaultRequestHeaders.AcceptLanguage.ParseAdd(language);

        var checkout = await anonymous.PostAsJsonAsync("/api/public/bookings", CheckoutPayload(longTerm.Id));
        var quote = await anonymous.PostAsJsonAsync("/api/public/bookings/quote", QuotePayload(longTerm.Id));

        foreach (var response in new[] { checkout, quote })
        {
            Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
            var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal(StableCode, problem.GetProperty("code").GetString());
            Assert.Contains(expectedText, problem.GetProperty("detail").GetString());
        }

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.False(await db.Bookings.IgnoreQueryFilters().AnyAsync(b => b.PropertyId == longTerm.Id));
    }

    [Fact]
    public async Task PublicQuote_ShortProperty_StillPrices()
    {
        var host = await SeedHostAsync("quote");
        var shortStay = await SeedPropertyAsync(host, RentalMode.Short, "Casa Breve");
        using var anonymous = _factory.CreateClient();

        var response = await anonymous.PostAsJsonAsync("/api/public/bookings/quote", QuotePayload(shortStay.Id));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Theory]
    [InlineData("it-IT", "è in modalità affitto lungo")]
    [InlineData("en", "set to long-term rental")]
    public async Task HostManualBookingAndQuote_LongProperty_Are422WithTheReasonNotTheGuestCapacity(string language, string expectedText)
    {
        var host = await SeedHostAsync("manual");
        var longTerm = await SeedPropertyAsync(host, RentalMode.Long, "Bilocale Lungo");
        using var client = _factory.CreateAuthenticatedClient(host.HostId, HostRole);
        client.DefaultRequestHeaders.AcceptLanguage.ParseAdd(language);
        var guestEmail = $"mario.{Guid.NewGuid():N}@example.com";

        var booking = await client.PostAsJsonAsync("/api/bookings", new
        {
            propertyId = longTerm.Id,
            checkInDate = Day(10).ToString("yyyy-MM-dd"),
            checkOutDate = Day(12).ToString("yyyy-MM-dd"),
            numberOfGuests = 2,
            guest = new { firstName = "Mario", lastName = "Rossi", email = guestEmail, phone = "+393331234567", country = "Italia" },
        });
        var quote = await client.PostAsJsonAsync("/api/bookings/quote", new
        {
            propertyId = longTerm.Id,
            checkInDate = Day(10).ToString("yyyy-MM-dd"),
            checkOutDate = Day(12).ToString("yyyy-MM-dd"),
            numberOfGuests = 2,
            numberOfChildren = 0,
        });

        foreach (var response in new[] { booking, quote })
        {
            Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
            var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
            // Not "too many guests (0)": the property has no capacity because it is let long-term.
            Assert.Equal(StableCode, problem.GetProperty("code").GetString());
            Assert.Contains(expectedText, problem.GetProperty("detail").GetString());
        }

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.False(await db.Bookings.IgnoreQueryFilters().AnyAsync(b => b.PropertyId == longTerm.Id));
        Assert.False(await db.Guests.IgnoreQueryFilters().AnyAsync(g => g.Email == guestEmail));
    }

    [Fact]
    public async Task HostManualBooking_ShortProperty_IsStillCreated()
    {
        var host = await SeedHostAsync("manual-short");
        var shortStay = await SeedPropertyAsync(host, RentalMode.Short, "Casa Breve");
        using var client = _factory.CreateAuthenticatedClient(host.HostId, HostRole);

        var response = await client.PostAsJsonAsync("/api/bookings", new
        {
            propertyId = shortStay.Id,
            checkInDate = Day(10).ToString("yyyy-MM-dd"),
            checkOutDate = Day(12).ToString("yyyy-MM-dd"),
            numberOfGuests = 2,
            guest = new
            {
                firstName = "Mario",
                lastName = "Rossi",
                email = $"mario.{Guid.NewGuid():N}@example.com",
                phone = "+393331234567",
                country = "Italia",
            },
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Fact]
    public async Task ActivationOfTheListing_LongProperty_Is422()
    {
        var host = await SeedHostAsync("activation");
        var longTerm = await SeedPropertyAsync(host, RentalMode.Long, "Bilocale Lungo");
        await SetAsync(longTerm.Id, p => p.ComplianceStatus = PropertyComplianceStatus.Pending);
        using var client = _factory.CreateAuthenticatedClient(host.HostId, HostRole);

        var response = await client.PostAsJsonAsync(
            $"/api/properties/{longTerm.Id}/compliance/activation/complete", new { tosAccepted = true });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal(StableCode, (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
        Assert.Equal(PropertyComplianceStatus.Pending, (await LoadAsync(longTerm.Id)).ComplianceStatus);
    }

    // ─── Host lists and records ─────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task HostList_ModeFilter_NarrowsTheListAndWithoutItNothingChanges()
    {
        var host = await SeedHostAsync("list");
        var shortStay = await SeedPropertyAsync(host, RentalMode.Short, "Casa Breve");
        var longTerm = await SeedPropertyAsync(host, RentalMode.Long, "Bilocale Lungo");
        using var client = _factory.CreateAuthenticatedClient(host.HostId, HostRole);

        Assert.Equal([shortStay.Id], await ListIdsAsync(client, "?mode=short"));
        Assert.Equal([longTerm.Id], await ListIdsAsync(client, "?mode=long"));
        Assert.Equal([longTerm.Id], await ListIdsAsync(client, "?mode=LONG"));
        Assert.Equal(new[] { shortStay.Id, longTerm.Id }.Order(), (await ListIdsAsync(client, string.Empty)).Order());
        Assert.Equal(new[] { shortStay.Id, longTerm.Id }.Order(), (await ListIdsAsync(client, "?mode=")).Order());

        var all = await client.GetFromJsonAsync<JsonElement>("/api/properties");
        var modes = all.EnumerateArray().ToDictionary(p => p.GetProperty("id").GetGuid(), p => p.GetProperty("rentalMode").GetString());
        Assert.Equal("Short", modes[shortStay.Id]);
        Assert.Equal("Long", modes[longTerm.Id]);
    }

    [Fact]
    public async Task HostList_ModeFilter_NeverShowsAnotherOrgsProperties()
    {
        var hostA = await SeedHostAsync("iso-a");
        var hostB = await SeedHostAsync("iso-b");
        var longOfA = await SeedPropertyAsync(hostA, RentalMode.Long, "Lungo di A");
        var longOfB = await SeedPropertyAsync(hostB, RentalMode.Long, "Lungo di B");
        using var clientB = _factory.CreateAuthenticatedClient(hostB.HostId, HostRole);

        Assert.Equal([longOfB.Id], await ListIdsAsync(clientB, "?mode=long"));
        Assert.Empty(await ListIdsAsync(clientB, "?mode=short"));
        Assert.Equal(HttpStatusCode.NotFound, (await clientB.GetAsync($"/api/properties/{longOfA.Id}")).StatusCode);
    }

    [Theory]
    [InlineData("both")]
    [InlineData("1")]
    [InlineData("lungo")]
    public async Task HostList_UnknownMode_Is400WithTheStableCode(string mode)
    {
        var host = await SeedHostAsync("list-400");
        await SeedPropertyAsync(host, RentalMode.Short, "Casa Breve");
        using var client = _factory.CreateAuthenticatedClient(host.HostId, HostRole);

        var response = await client.GetAsync($"/api/properties?mode={mode}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("validation_error", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
    }

    [Fact]
    public async Task HostRecords_CarryTheRentalMode()
    {
        var host = await SeedHostAsync("records");
        var longTerm = await SeedPropertyAsync(host, RentalMode.Long, "Bilocale Lungo");
        using var client = _factory.CreateAuthenticatedClient(host.HostId, HostRole);

        var record = await client.GetFromJsonAsync<JsonElement>($"/api/properties/{longTerm.Id}");
        var detail = await client.GetFromJsonAsync<JsonElement>($"/api/properties/{longTerm.Id}/detail");

        Assert.Equal("Long", record.GetProperty("rentalMode").GetString());
        Assert.Equal("Long", detail.GetProperty("rentalMode").GetString());
    }

    [Fact]
    public async Task PlanLimit_CountsTheLongTermPropertiesToo()
    {
        var host = await SeedHostAsync("plan");
        await SeedPropertyAsync(host, RentalMode.Short, "Casa Breve");
        await SeedPropertyAsync(host, RentalMode.Long, "Bilocale Lungo");
        await SeedPropertyAsync(host, RentalMode.Long, "Trilocale Lungo");
        using var client = _factory.CreateAuthenticatedClient(host.HostId, HostRole);

        var entitlement = await client.GetFromJsonAsync<JsonElement>("/api/orgs/me/entitlement");

        Assert.Equal(3, entitlement.GetProperty("usage").GetProperty("properties").GetInt32());
        Assert.False(entitlement.GetProperty("canAddProperty").GetBoolean());
    }

    // ─── Creation and update ────────────────────────────────────────────────────────────────────────

    [Theory]
    // The long-rent form of the landlords: no guests, no rate, no mode.
    [InlineData(0, 0, null, "Long")]
    [InlineData(4, 120, null, "Short")]
    [InlineData(0, 0, "Short", "Short")]
    [InlineData(3, 90, "Long", "Long")]
    public async Task Create_Mode_FollowsTheRequestOrTheCompatibilityRule(int maxGuests, int nightlyRate, string? sent, string expected)
    {
        var host = await SeedHostAsync("create");
        using var client = _factory.CreateAuthenticatedClient(host.HostId, HostRole);

        var response = await client.PostAsJsonAsync("/api/properties", CreateBody(maxGuests, nightlyRate, sent));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(expected, created.GetProperty("rentalMode").GetString());
        var stored = await LoadAsync(created.GetProperty("id").GetGuid());
        Assert.Equal(Enum.Parse<RentalMode>(expected), stored.RentalMode);
    }

    [Fact]
    public async Task Create_UnknownMode_Is400()
    {
        var host = await SeedHostAsync("create-400");
        using var client = _factory.CreateAuthenticatedClient(host.HostId, HostRole);

        var response = await client.PostAsJsonAsync("/api/properties", CreateBody(4, 100, "Both"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Theory]
    [InlineData(RentalMode.Short, "Long")]
    [InlineData(RentalMode.Long, "Short")]
    public async Task Update_DifferentMode_Is422AndNothingIsSaved(RentalMode stored, string requested)
    {
        var host = await SeedHostAsync("update-422");
        var property = await SeedPropertyAsync(host, stored, "Casa");
        using var client = _factory.CreateAuthenticatedClient(host.HostId, HostRole);

        var response = await client.PutAsJsonAsync(
            $"/api/properties/{property.Id}", new { name = "Rinominata", rentalMode = requested });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal(
            "property_rental_mode_change_not_allowed",
            (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
        var after = await LoadAsync(property.Id);
        Assert.Equal("Casa", after.Name);
        Assert.Equal(stored, after.RentalMode);
    }

    [Theory]
    [InlineData(RentalMode.Short)]
    [InlineData(RentalMode.Long)]
    public async Task Update_SameModeOrNone_SavesTheOtherFieldsAndKeepsTheMode(RentalMode stored)
    {
        var host = await SeedHostAsync("update-204");
        var property = await SeedPropertyAsync(host, stored, "Casa");
        using var client = _factory.CreateAuthenticatedClient(host.HostId, HostRole);

        var withMode = await client.PutAsJsonAsync(
            $"/api/properties/{property.Id}", new { name = "Prima", rentalMode = stored.ToString() });
        var withoutMode = await client.PutAsJsonAsync($"/api/properties/{property.Id}", new { name = "Seconda" });

        Assert.Equal(HttpStatusCode.NoContent, withMode.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, withoutMode.StatusCode);
        var after = await LoadAsync(property.Id);
        Assert.Equal("Seconda", after.Name);
        Assert.Equal(stored, after.RentalMode);
    }

    // ─── Seed and helpers ───────────────────────────────────────────────────────────────────────────

    private sealed record Host(string HostId, Guid OrgId, string OrgSlug);

    /// <summary>A host with an org that takes payments (so that a short-rent property is bookable and published).</summary>
    private async Task<Host> SeedHostAsync(string label)
    {
        // No "auth0|" in the id: the factory names the org "test-org-{id}", and the SEO and sitemap routes refuse a slug that
        // is not lower case letters, digits and hyphens.
        var hostId = $"pm01-{label}-{Guid.NewGuid():N}";
        var org = await _factory.SeedOrgForOwnerAsync(hostId);
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var stored = await db.Orgs.SingleAsync(o => o.Id == org.Id);
        stored.StripeConnectedAccountId = $"acct_pm01_{Guid.NewGuid():N}";
        stored.ConnectChargesEnabled = true;
        await db.SaveChangesAsync();
        return new Host(hostId, org.Id, stored.Slug);
    }

    /// <summary>
    /// A property of the host's org in the given mode that is published whatever the mode (active, not paused, compliance
    /// activated, valid CIN), with a slug: only the mode decides whether the public sees it.
    /// </summary>
    private async Task<Property> SeedPropertyAsync(Host host, RentalMode mode, string name, string? city = null)
    {
        var seeded = await _factory.SeedPropertyAsync(host.HostId);
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var property = await db.Properties.SingleAsync(p => p.Id == seeded.Id);
        property.Name = name;
        property.Slug = $"{name.ToLowerInvariant().Replace(' ', '-')}-{Guid.NewGuid().ToString("N")[..8]}";
        property.City = city ?? "Roma";
        property.CinCode = "IT058091C27G5FFZDZ";
        property.ComplianceStatus = PropertyComplianceStatus.Active;
        property.RentalMode = mode;
        await db.SaveChangesAsync();
        return property;
    }

    private static IEnumerable<string> PublicPaths(Host host, Property property) =>
    [
        $"/api/properties/{property.Id}/public",
        $"/api/public/orgs/{host.OrgSlug}/properties/{property.Id}",
        $"/api/public/orgs/{host.OrgSlug}/properties/{property.Slug}",
        $"/api/public/seo/orgs/{host.OrgSlug}/properties/{property.Slug}",
        $"/api/public/bookings/property/{property.Id}/availability?startDate={Day(10):yyyy-MM-dd}&endDate={Day(20):yyyy-MM-dd}",
    ];

    private static DateTime Day(int plusDays) =>
        TimeProvider.System.TodayInRome().AddDays(plusDays);

    private static object QuotePayload(Guid propertyId) => new
    {
        propertyId,
        checkInDate = Day(10).ToString("yyyy-MM-dd"),
        checkOutDate = Day(12).ToString("yyyy-MM-dd"),
        numberOfAdults = 2,
        numberOfChildren = 0,
    };

    private static object CheckoutPayload(Guid propertyId) => new
    {
        propertyId,
        checkInDate = Day(10).ToString("yyyy-MM-dd"),
        checkOutDate = Day(12).ToString("yyyy-MM-dd"),
        numberOfAdults = 2,
        numberOfChildren = 0,
        guest = new
        {
            firstName = "Giulia",
            lastName = "Bianchi",
            email = $"giulia.{Guid.NewGuid():N}@example.com",
            phone = "+393339876543",
            country = "IT",
        },
        consent = new { dataProcessing = true, consentVersion = ConsentVersion },
        paymentOption = nameof(PaymentOption.Immediate),
    };

    private static object CreateBody(int maxGuests, int nightlyRate, string? rentalMode) => new Dictionary<string, object?>
    {
        ["name"] = "Bilocale PM-01",
        ["description"] = "Bilocale",
        ["address"] = $"Via Italia {Guid.NewGuid():N}",
        ["city"] = "Monza",
        ["postalCode"] = "20900",
        ["bedrooms"] = 2,
        ["bathrooms"] = 1,
        ["maxGuests"] = maxGuests,
        ["nightlyRate"] = nightlyRate,
        ["rentalMode"] = rentalMode,
    };

    private static async Task<List<Guid>> ListIdsAsync(HttpClient client, string query)
    {
        var rows = await client.GetFromJsonAsync<JsonElement>($"/api/properties{query}");
        return rows.EnumerateArray().Select(p => p.GetProperty("id").GetGuid()).ToList();
    }

    private async Task<Property> LoadAsync(Guid propertyId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.Properties.IgnoreQueryFilters().AsNoTracking().SingleAsync(p => p.Id == propertyId);
    }

    private async Task SetAsync(Guid propertyId, Action<Property> change)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var property = await db.Properties.IgnoreQueryFilters().SingleAsync(p => p.Id == propertyId);
        change(property);
        await db.SaveChangesAsync();
    }
}
