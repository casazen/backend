using System.Net;
using System.Text;
using System.Text.Json;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Utilities;
using Casazen.Infrastructure.Data;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using PlanTierEnum = Casazen.Core.Entities.Enums.PlanTier;

namespace Casazen.Tests.Integration;

/// <summary>The default integration factory with a versioned text for the optional "send me offers" consent (CO-15).</summary>
public sealed class MarketingConsentConfiguredFactory : CasazenWebApplicationFactory
{
    public const string Version = "marketing-2026-10-test";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureAppConfiguration((_, config) =>
            config.AddInMemoryCollection(new Dictionary<string, string?> { ["Gdpr:MarketingConsentVersion"] = Version }));
    }
}

/// <summary>
/// DB-03: the optional "send me offers" box of the checkout. Recorded on the guest of the booking and in the append-only
/// register of the privacy history (CO-15) with the version of the text, only when the text has a version; the checkout
/// without the box is the one of today.
/// </summary>
public class DirectCheckoutMarketingConsentIntegrationTests : IClassFixture<MarketingConsentConfiguredFactory>
{
    private const string ConsentVersion = "2026-06-direct-checkout-v1";
    private readonly MarketingConsentConfiguredFactory _factory;

    public DirectCheckoutMarketingConsentIntegrationTests(MarketingConsentConfiguredFactory factory)
    {
        _factory = factory;
        FakeStripeService.Reset();
    }

    [Fact]
    public async Task PublicOrg_WithAVersionedText_OffersTheBoxWithItsVersion()
    {
        var property = await SeedPropertyAsync();
        using var client = _factory.CreateClient();

        var dto = JsonDocument.Parse(await client.GetStringAsync($"/api/public/orgs/{await SlugOfAsync(property.OrgId)}")).RootElement;

        Assert.Equal(MarketingConsentConfiguredFactory.Version, dto.GetProperty("marketingConsentVersion").GetString());
    }

    [Fact]
    public async Task Booking_WithTheBoxTicked_RecordsTheConsentOnTheGuestAndInTheRegisterWithItsVersion()
    {
        var property = await SeedPropertyAsync();
        using var client = _factory.CreateClient();
        var before = DateTime.UtcNow.AddMinutes(-1);

        var response = await PostAsync(client, Payload(property.Id, marketingConsent: true));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var bookingId = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("bookingId").GetGuid();
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var booking = await db.Bookings.IgnoreQueryFilters().AsNoTracking().Include(b => b.Guest).SingleAsync(b => b.Id == bookingId);
        Assert.True(booking.Guest.MarketingConsent);
        Assert.InRange(booking.Guest.MarketingConsentDate!.Value, before, DateTime.UtcNow.AddMinutes(1));

        var record = await db.GuestConsentRecords.IgnoreQueryFilters().AsNoTracking().SingleAsync(r => r.GuestId == booking.GuestId);
        Assert.Equal(property.OrgId, record.OrgId);
        Assert.Equal(GuestConsentPurpose.Marketing, record.Purpose);
        Assert.Equal(GuestConsentAction.Granted, record.Action);
        Assert.Equal(MarketingConsentConfiguredFactory.Version, record.Version);
        Assert.Equal(GuestConsentSource.BookingCheckout, record.Source);
        Assert.Equal(booking.Guest.MarketingConsentDate, record.RecordedAt);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(null)]
    public async Task Booking_WithoutTheBox_RecordsNoMarketingConsentAndStaysTheCheckoutOfToday(bool? marketingConsent)
    {
        var property = await SeedPropertyAsync();
        using var client = _factory.CreateClient();

        var response = await PostAsync(client, Payload(property.Id, marketingConsent));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var bookingId = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("bookingId").GetGuid();
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var booking = await db.Bookings.IgnoreQueryFilters().AsNoTracking().Include(b => b.Guest).SingleAsync(b => b.Id == bookingId);
        Assert.False(booking.Guest.MarketingConsent);
        Assert.Null(booking.Guest.MarketingConsentDate);
        Assert.False(await db.GuestConsentRecords.IgnoreQueryFilters().AnyAsync(r => r.GuestId == booking.GuestId));
    }

    [Fact]
    public async Task Booking_PayAtTheProperty_WithTheBoxTicked_RecordsTheConsentToo()
    {
        var property = await SeedPropertyAsync();
        using var client = _factory.CreateClient();

        var response = await PostAsync(client, Payload(property.Id, marketingConsent: true, paymentOption: "OnSite"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var bookingId = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("bookingId").GetGuid();
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var guestId = await db.Bookings.IgnoreQueryFilters().Where(b => b.Id == bookingId).Select(b => b.GuestId).SingleAsync();
        Assert.True(await db.GuestConsentRecords.IgnoreQueryFilters().AnyAsync(r => r.GuestId == guestId && r.Purpose == GuestConsentPurpose.Marketing));
    }

    [Fact]
    public async Task Booking_DatesNotFree_LeavesNoConsentBehind()
    {
        var property = await SeedPropertyAsync();
        using var client = _factory.CreateClient();
        var first = await PostAsync(client, Payload(property.Id, marketingConsent: false));
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        var second = await PostAsync(client, Payload(property.Id, marketingConsent: true));

        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.False(await db.GuestConsentRecords.IgnoreQueryFilters().AnyAsync(r => r.OrgId == property.OrgId));
        Assert.Equal(1, await db.Guests.IgnoreQueryFilters().CountAsync(g => g.OrgId == property.OrgId));
    }

    // ─── Helpers ────────────────────────────────────────────────────────────────────────────────────

    private static object Payload(Guid propertyId, bool? marketingConsent, string paymentOption = "Immediate")
    {
        var checkIn = TimeProvider.System.TodayInRome().AddDays(40);
        var fields = new Dictionary<string, object?>
        {
            ["propertyId"] = propertyId,
            ["checkInDate"] = checkIn.ToString("yyyy-MM-dd"),
            ["checkOutDate"] = checkIn.AddDays(3).ToString("yyyy-MM-dd"),
            ["numberOfAdults"] = 2,
            ["numberOfChildren"] = 0,
            ["guest"] = new
            {
                firstName = "Mario",
                lastName = "Rossi",
                email = $"mario.{Guid.NewGuid():N}@example.com",
                phone = "+393331234567",
                country = "IT",
            },
            ["consent"] = new { dataProcessing = true, consentVersion = ConsentVersion },
            ["paymentOption"] = paymentOption,
        };
        if (marketingConsent is { } value)
            fields["marketingConsent"] = value;

        return fields;
    }

    private static Task<HttpResponseMessage> PostAsync(HttpClient client, object payload) =>
        client.PostAsync("/api/public/bookings", new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"));

    private async Task<string> SlugOfAsync(Guid orgId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.Orgs.AsNoTracking().Where(o => o.Id == orgId).Select(o => o.Slug).SingleAsync();
    }

    private async Task<Property> SeedPropertyAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var org = new OrgEntity
        {
            Name = "Marketing Consent Org",
            Slug = $"mkt-{Guid.NewGuid():N}",
            DisplayName = "Marketing Consent Org",
            ContactEmail = "mkt@example.com",
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
            Name = "Marketing Consent Villa",
            Slug = $"villa-{Guid.NewGuid():N}",
            Description = "Integration test property",
            Address = $"Via Consenso {Guid.NewGuid():N}",
            City = "Seveso",
            PostalCode = "20822",
            Bedrooms = 2,
            Bathrooms = 1,
            MaxGuests = 4,
            NightlyRate = 100m,
            CleaningFee = 20m,
            CinCode = "IT108040C2ABCDEFGH",
            IsActive = true,
            ComplianceStatus = PropertyComplianceStatus.Active,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        db.Properties.Add(property);
        await db.SaveChangesAsync();
        return property;
    }
}

/// <summary>
/// DB-03 on the default factory (no versioned text for the marketing consent, as in every environment today): the box is
/// not offered and a checkout that ticks it is refused with a stable code, without leaving anything behind.
/// </summary>
public class DirectCheckoutMarketingConsentWithoutVersionIntegrationTests : IClassFixture<CasazenWebApplicationFactory>
{
    private const string ConsentVersion = "2026-06-direct-checkout-v1";
    private readonly CasazenWebApplicationFactory _factory;

    public DirectCheckoutMarketingConsentWithoutVersionIntegrationTests(CasazenWebApplicationFactory factory)
    {
        _factory = factory;
        FakeStripeService.Reset();
    }

    [Fact]
    public async Task Booking_WithTheBoxTickedButNoVersionedText_Returns422AndStoresNothing()
    {
        var property = await SeedPropertyAsync();
        using var client = _factory.CreateClient();

        var response = await client.PostAsync(
            "/api/public/bookings",
            new StringContent(JsonSerializer.Serialize(Payload(property.Id, marketingConsent: true)), Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("direct_booking_marketing_consent_unavailable", problem.GetProperty("code").GetString());
        Assert.False(string.IsNullOrWhiteSpace(problem.GetProperty("detail").GetString()));
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.False(await db.Bookings.IgnoreQueryFilters().AnyAsync(b => b.PropertyId == property.Id));
        Assert.False(await db.Guests.IgnoreQueryFilters().AnyAsync(g => g.OrgId == property.OrgId));
        Assert.False(await db.GuestConsentRecords.IgnoreQueryFilters().AnyAsync(r => r.OrgId == property.OrgId));
    }

    [Fact]
    public async Task Booking_WithoutTheBox_IsTheCheckoutOfToday()
    {
        var property = await SeedPropertyAsync();
        using var client = _factory.CreateClient();

        var response = await client.PostAsync(
            "/api/public/bookings",
            new StringContent(JsonSerializer.Serialize(Payload(property.Id, marketingConsent: null)), Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("false")]
    public async Task Booking_WithTheBoxNullOrFalse_IsTheCheckoutOfTodayEvenWithoutAVersionedText(string json)
    {
        var property = await SeedPropertyAsync();
        using var client = _factory.CreateClient();
        // The member is in the body, with a value that is not "yes": no 400 for a null, no 422 for the missing text.
        var body = JsonSerializer.Serialize(Payload(property.Id, marketingConsent: null));
        body = body.TrimEnd('}') + $",\"marketingConsent\":{json}}}";

        var response = await client.PostAsync("/api/public/bookings", new StringContent(body, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var bookingId = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("bookingId").GetGuid();
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var guest = await db.Bookings.IgnoreQueryFilters().AsNoTracking().Where(b => b.Id == bookingId).Select(b => b.Guest).SingleAsync();
        Assert.False(guest.MarketingConsent);
    }

    private static Dictionary<string, object?> Payload(Guid propertyId, bool? marketingConsent)
    {
        var checkIn = TimeProvider.System.TodayInRome().AddDays(40);
        var fields = new Dictionary<string, object?>
        {
            ["propertyId"] = propertyId,
            ["checkInDate"] = checkIn.ToString("yyyy-MM-dd"),
            ["checkOutDate"] = checkIn.AddDays(3).ToString("yyyy-MM-dd"),
            ["numberOfAdults"] = 2,
            ["numberOfChildren"] = 0,
            ["guest"] = new
            {
                firstName = "Mario",
                lastName = "Rossi",
                email = $"mario.{Guid.NewGuid():N}@example.com",
                phone = "+393331234567",
                country = "IT",
            },
            ["consent"] = new { dataProcessing = true, consentVersion = ConsentVersion },
            ["paymentOption"] = "Immediate",
        };
        if (marketingConsent is { } value)
            fields["marketingConsent"] = value;

        return fields;
    }

    private async Task<Property> SeedPropertyAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var org = new OrgEntity
        {
            Name = "Marketing Consent Org",
            Slug = $"mkt0-{Guid.NewGuid():N}",
            DisplayName = "Marketing Consent Org",
            ContactEmail = "mkt@example.com",
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
            Name = "Marketing Consent Villa",
            Slug = $"villa-{Guid.NewGuid():N}",
            Description = "Integration test property",
            Address = $"Via Consenso {Guid.NewGuid():N}",
            City = "Seveso",
            PostalCode = "20822",
            Bedrooms = 2,
            Bathrooms = 1,
            MaxGuests = 4,
            NightlyRate = 100m,
            CleaningFee = 20m,
            CinCode = "IT108040C2ABCDEFGH",
            IsActive = true,
            ComplianceStatus = PropertyComplianceStatus.Active,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        db.Properties.Add(property);
        await db.SaveChangesAsync();
        return property;
    }
}
