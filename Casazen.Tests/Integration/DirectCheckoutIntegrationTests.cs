using System.Net;
using System.Text;
using System.Text.Json;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using PlanTierEnum = Casazen.Core.Entities.Enums.PlanTier;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.External;
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
        DateTime? checkOut = null)
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
                firstName = "Mario",
                lastName = "Rossi",
                email = $"mario.{Guid.NewGuid():N}@example.com",
                phone = "+393331234567",
                country = "IT",
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

    private async Task<Property> SeedConnectReadyPropertyAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var org = new Org
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

        var org = new Org
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
}
