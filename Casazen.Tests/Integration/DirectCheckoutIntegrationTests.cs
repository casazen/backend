using System.Net;
using System.Text;
using System.Text.Json;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using PlanTierEnum = Casazen.Core.Entities.Enums.PlanTier;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.External;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Stripe;
using Xunit;

namespace Casazen.Tests.Integration;

public class DirectCheckoutIntegrationTests : IClassFixture<CasazenWebApplicationFactory>
{
    private const string ConsentVersion = "2026-06-direct-checkout-v1";
    private readonly CasazenWebApplicationFactory _factory;

    public DirectCheckoutIntegrationTests(CasazenWebApplicationFactory factory)
    {
        _factory = factory;
        FakeStripeService.Reset();
    }

    [Fact]
    public async Task AC1_MissingConsent_Returns400()
    {
        var property = await SeedConnectReadyPropertyAsync();
        var client = _factory.CreateClient();

        var payload = BuildPayload(property.Id, consent: false);
        var response = await PostDirectBookingAsync(client, payload);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task AC1_InvalidDates_Returns400()
    {
        var property = await SeedConnectReadyPropertyAsync();
        var client = _factory.CreateClient();

        var payload = BuildPayload(
            property.Id,
            checkIn: DateTime.UtcNow.Date.AddDays(10),
            checkOut: DateTime.UtcNow.Date.AddDays(5));
        var response = await PostDirectBookingAsync(client, payload);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task AC3_OperatorNotOnboarded_Returns409()
    {
        var property = await SeedPropertyWithoutConnectAsync();
        var client = _factory.CreateClient();

        var response = await PostDirectBookingAsync(client, BuildPayload(property.Id));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var json = await response.Content.ReadAsStringAsync();
        Assert.Contains("Complete Stripe onboarding", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SuccessfulBooking_ReturnsClientSecretAndPublishableContext()
    {
        var property = await SeedConnectReadyPropertyAsync();
        var client = _factory.CreateClient();

        var response = await PostDirectBookingAsync(client, BuildPayload(property.Id));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = doc.RootElement;

        Assert.False(string.IsNullOrWhiteSpace(root.GetProperty("clientSecret").GetString()));
        Assert.Equal("pi_test_secret_direct", root.GetProperty("clientSecret").GetString());
        Assert.True(root.GetProperty("bookingId").GetGuid() != Guid.Empty);

        var ctx = root.GetProperty("connectedAccountPublishableContext");
        Assert.Equal("pk_test_integration", ctx.GetProperty("publishableKey").GetString());
        Assert.Equal("acct_test_connect_ready", ctx.GetProperty("stripeAccountId").GetString());
        Assert.True(root.GetProperty("amount").GetDecimal() > 0);
    }

    [Fact]
    public async Task DirectBooking_WithExistingGuestEmail_DoesNotOverwriteExistingGuest()
    {
        var property = await SeedConnectReadyPropertyAsync();
        var email = $"existing.{Guid.NewGuid():N}@example.com";
        Guid existingGuestId;

        using (var seedScope = _factory.Services.CreateScope())
        {
            var db = seedScope.ServiceProvider.GetRequiredService<AppDbContext>();
            var existingGuest = new Guest
            {
                FirstName = "Original",
                LastName = "Guest",
                Email = email,
                PhoneNumber = "+390000000000",
                Country = "FR",
                DataProcessingPurpose = "Existing booking",
                CreatedAt = DateTime.UtcNow.AddDays(-10),
                UpdatedAt = DateTime.UtcNow.AddDays(-10),
            };
            db.Guests.Add(existingGuest);
            await db.SaveChangesAsync();
            existingGuestId = existingGuest.Id;
        }

        var client = _factory.CreateClient();
        var response = await PostDirectBookingAsync(
            client,
            BuildPayload(
                property.Id,
                guestEmail: email,
                guestFirstName: "Injected",
                guestLastName: "Profile",
                guestPhone: "+399999999999",
                guestCountry: "IT"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var verifyScope = _factory.Services.CreateScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var originalGuest = await verifyDb.Guests.AsNoTracking().SingleAsync(g => g.Id == existingGuestId);
        Assert.Equal("Original", originalGuest.FirstName);
        Assert.Equal("Guest", originalGuest.LastName);
        Assert.Equal("+390000000000", originalGuest.PhoneNumber);
        Assert.Equal("FR", originalGuest.Country);
        Assert.Equal("Existing booking", originalGuest.DataProcessingPurpose);

        var booking = await verifyDb.Bookings
            .AsNoTracking()
            .Include(b => b.Guest)
            .SingleAsync(b => b.PropertyId == property.Id);
        Assert.NotEqual(existingGuestId, booking.GuestId);
        Assert.Equal(email, booking.Guest.Email);
        Assert.Equal("Injected", booking.Guest.FirstName);
        Assert.Equal("Profile", booking.Guest.LastName);
        Assert.Equal(2, await verifyDb.Guests.CountAsync(g => g.Email == email));
    }

    [Fact]
    public async Task Lookup_RequiresBookingIdAndMatchingEmail()
    {
        var property = await SeedConnectReadyPropertyAsync();
        var email = $"lookup.{Guid.NewGuid():N}@example.com";
        var client = _factory.CreateClient();

        var createResponse = await PostDirectBookingAsync(client, BuildPayload(property.Id, guestEmail: email));
        Assert.Equal(HttpStatusCode.OK, createResponse.StatusCode);
        using var createDoc = JsonDocument.Parse(await createResponse.Content.ReadAsStringAsync());
        var bookingId = createDoc.RootElement.GetProperty("bookingId").GetGuid();

        var missingBookingIdResponse = await PostLookupAsync(client, new { email });
        Assert.Equal(HttpStatusCode.BadRequest, missingBookingIdResponse.StatusCode);

        var wrongEmailResponse = await PostLookupAsync(client, new
        {
            bookingId,
            email = $"other.{Guid.NewGuid():N}@example.com",
        });
        Assert.Equal(HttpStatusCode.OK, wrongEmailResponse.StatusCode);
        using (var wrongEmailDoc = JsonDocument.Parse(await wrongEmailResponse.Content.ReadAsStringAsync()))
        {
            Assert.Empty(wrongEmailDoc.RootElement.GetProperty("bookings").EnumerateArray());
        }

        var lookupResponse = await PostLookupAsync(client, new { bookingId, email });
        Assert.Equal(HttpStatusCode.OK, lookupResponse.StatusCode);
        using var lookupDoc = JsonDocument.Parse(await lookupResponse.Content.ReadAsStringAsync());
        var booking = Assert.Single(lookupDoc.RootElement.GetProperty("bookings").EnumerateArray());
        Assert.Equal(bookingId, booking.GetProperty("bookingId").GetGuid());
        Assert.Equal("Direct Checkout Villa", booking.GetProperty("propertyName").GetString());
    }

    [Fact]
    public async Task Webhook_ConfirmsDirectBooking()
    {
        var property = await SeedConnectReadyPropertyAsync();
        var client = _factory.CreateClient();
        var response = await PostDirectBookingAsync(client, BuildPayload(property.Id));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var bookingId = doc.RootElement.GetProperty("bookingId").GetGuid();
        var paymentIntentId = FakeStripeService.LastPaymentIntentId!;

        using var scope = _factory.Services.CreateScope();
        var handler = scope.ServiceProvider.GetRequiredService<StripeWebhookHandler>();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var paymentIntent = new PaymentIntent
        {
            Id = paymentIntentId,
            Amount = 65000,
            Metadata = new Dictionary<string, string>
            {
                ["kind"] = "direct-booking",
                ["bookingId"] = bookingId.ToString(),
            },
        };

        var stripeEvent = new Event
        {
            Type = "payment_intent.succeeded",
            Data = new EventData { Object = paymentIntent },
        };

        await handler.HandleEventAsync(stripeEvent, WebhookSource.Connected);

        var booking = await db.Bookings.FindAsync(bookingId);
        Assert.NotNull(booking);
        Assert.Equal(BookingStatus.Confirmed, booking!.Status);

        var payment = db.Payments.Single(p => p.BookingId == bookingId);
        Assert.Equal(PaymentStatus.Completed, payment.Status);
        Assert.Equal(paymentIntentId, payment.TransactionId);
    }

    private static object BuildPayload(
        Guid propertyId,
        bool consent = true,
        DateTime? checkIn = null,
        DateTime? checkOut = null,
        string? guestEmail = null,
        string guestFirstName = "Mario",
        string guestLastName = "Rossi",
        string guestPhone = "+393331234567",
        string guestCountry = "IT")
    {
        var inDate = checkIn ?? DateTime.UtcNow.Date.AddDays(30);
        var outDate = checkOut ?? inDate.AddDays(4);
        return new
        {
            propertyId,
            checkInDate = inDate.ToString("yyyy-MM-dd"),
            checkOutDate = outDate.ToString("yyyy-MM-dd"),
            numberOfAdults = 2,
            numberOfChildren = 0,
            guest = new
            {
                firstName = guestFirstName,
                lastName = guestLastName,
                email = guestEmail ?? $"mario.{Guid.NewGuid():N}@example.com",
                phone = guestPhone,
                country = guestCountry,
            },
            consent = new
            {
                dataProcessing = consent,
                consentVersion = ConsentVersion,
            },
        };
    }

    private static async Task<HttpResponseMessage> PostDirectBookingAsync(HttpClient client, object payload)
    {
        var json = JsonSerializer.Serialize(payload);
        return await client.PostAsync(
            "/api/public/bookings",
            new StringContent(json, Encoding.UTF8, "application/json"));
    }

    private static async Task<HttpResponseMessage> PostLookupAsync(HttpClient client, object payload)
    {
        var json = JsonSerializer.Serialize(payload);
        return await client.PostAsync(
            "/api/public/bookings/lookup",
            new StringContent(json, Encoding.UTF8, "application/json"));
    }

    private async Task<Property> SeedConnectReadyPropertyAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var org = new OrgEntity
        {
            Name = "Direct Checkout Org",
            Slug = $"direct-{Guid.NewGuid():N}",
            DisplayName = "Direct Checkout Org",
            ContactEmail = "checkout@example.com",
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
            Name = "Direct Checkout Villa",
            Description = "Integration test property",
            Address = $"Via Direct {Guid.NewGuid():N}",
            City = "Rome",
            PostalCode = "00100",
            Latitude = 41.9028m,
            Longitude = 12.4964m,
            Bedrooms = 2,
            Bathrooms = 1,
            MaxGuests = 4,
            NightlyRate = 150m,
            CleaningFee = 50m,
            DamageDeposit = 200m,
            CinCode = "IT-12345-0123456789",
            IsActive = true,
            ComplianceStatus = PropertyComplianceStatus.Active,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        db.Properties.Add(property);
        await db.SaveChangesAsync();
        return property;
    }

    private async Task<Property> SeedPropertyWithoutConnectAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var org = new OrgEntity
        {
            Name = "No Connect Org",
            Slug = $"no-connect-{Guid.NewGuid():N}",
            DisplayName = "No Connect Org",
            ContactEmail = "noconnect@example.com",
            PlanTier = PlanTierEnum.Starter,
            IsActive = true,
        };
        db.Orgs.Add(org);

        var property = new Property
        {
            OwnerId = $"auth0|owner-{Guid.NewGuid():N}",
            OrgId = org.Id,
            Name = "No Connect Villa",
            Description = "Integration test property",
            Address = $"Via NoConnect {Guid.NewGuid():N}",
            City = "Rome",
            PostalCode = "00100",
            Latitude = 41.9028m,
            Longitude = 12.4964m,
            Bedrooms = 2,
            Bathrooms = 1,
            MaxGuests = 4,
            NightlyRate = 150m,
            CleaningFee = 50m,
            DamageDeposit = 200m,
            CinCode = "IT-12345-0123456789",
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

internal sealed class FakeStripeService : IStripeService
{
    public static string? LastPaymentIntentId { get; private set; }
    public static string LastClientSecret { get; private set; } = "pi_test_secret_direct";

    public static void Reset()
    {
        LastPaymentIntentId = null;
        LastClientSecret = "pi_test_secret_direct";
    }

    public Task<PaymentIntent> CreatePaymentIntentAsync(
        long amount,
        string currency,
        Dictionary<string, string> metadata)
    {
        LastPaymentIntentId = $"pi_test_{Guid.NewGuid():N}";
        return Task.FromResult(new PaymentIntent
        {
            Id = LastPaymentIntentId,
            ClientSecret = LastClientSecret,
            Amount = amount,
            Currency = currency,
        });
    }

    public Task<PaymentIntent> CreateConnectedAccountPaymentIntentAsync(
        string connectedAccountId,
        long amountCents,
        string currency,
        Dictionary<string, string> metadata)
    {
        LastPaymentIntentId = $"pi_test_{Guid.NewGuid():N}";
        return Task.FromResult(new PaymentIntent
        {
            Id = LastPaymentIntentId,
            ClientSecret = LastClientSecret,
            Amount = amountCents,
            Currency = currency,
            Metadata = metadata,
        });
    }

    public Task<PaymentIntent> ConfirmPaymentAsync(string paymentIntentId) =>
        Task.FromResult(new PaymentIntent { Id = paymentIntentId });

    public Task<Refund> RefundPaymentAsync(string paymentIntentId, long? amount = null) =>
        Task.FromResult(new Refund { Id = "re_test", PaymentIntentId = paymentIntentId });

    public Task<SetupIntent> CreateConnectedAccountSetupIntentAsync(
        string connectedAccountId,
        Dictionary<string, string> metadata)
    {
        return Task.FromResult(new SetupIntent
        {
            Id = $"seti_test_{Guid.NewGuid():N}",
            ClientSecret = "seti_test_secret",
            PaymentMethodId = $"pm_test_{Guid.NewGuid():N}",
            Metadata = metadata,
        });
    }

    public Task<PaymentIntent> ChargePaymentMethodAsync(
        string connectedAccountId,
        string customerId,
        string paymentMethodId,
        long amountCents,
        string currency,
        Dictionary<string, string> metadata)
    {
        LastPaymentIntentId = $"pi_test_{Guid.NewGuid():N}";
        return Task.FromResult(new PaymentIntent
        {
            Id = LastPaymentIntentId,
            Amount = amountCents,
            Currency = currency,
            Metadata = metadata,
        });
    }
}
