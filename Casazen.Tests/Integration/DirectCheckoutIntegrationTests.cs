using System.Net;
using System.Text;
using System.Text.Json;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;
using Casazen.Core.Utilities;
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
    public async Task AC1_InvalidDates_Returns422WithStableCode()
    {
        var property = await SeedConnectReadyPropertyAsync();
        var client = _factory.CreateClient();

        var payload = BuildPayload(
            property.Id,
            checkIn: DateTime.UtcNow.Date.AddDays(10),
            checkOut: DateTime.UtcNow.Date.AddDays(5));
        var response = await PostDirectBookingAsync(client, payload);

        // BK-06: a business rule of the checkout is a 422 ProblemDetails with a code the frontend translates.
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("direct_booking_invalid_stay", problem.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task AC3_OperatorNotOnboarded_Returns409()
    {
        var property = await SeedPropertyWithoutConnectAsync();
        var client = _factory.CreateClient();

        var response = await PostDirectBookingAsync(client, BuildPayload(property.Id));

        // R-11: stable code and localized message instead of an English text the frontend did not show.
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("direct_booking_payments_not_ready", problem.RootElement.GetProperty("code").GetString());
        Assert.False(string.IsNullOrWhiteSpace(problem.RootElement.GetProperty("detail").GetString()));
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
                OrgId = property.OrgId,
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

    [Fact]
    public async Task DeferredBooking_SetupWebhook_PersistsCustomerAndConfirmsBooking()
    {
        var property = await SeedConnectReadyPropertyAsync();
        var client = _factory.CreateClient();
        var response = await PostDirectBookingAsync(
            client,
            BuildPayload(property.Id, paymentOption: PaymentOption.OnCancellationDeadline));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var bookingId = doc.RootElement.GetProperty("bookingId").GetGuid();

        using var scope = _factory.Services.CreateScope();
        var handler = scope.ServiceProvider.GetRequiredService<StripeWebhookHandler>();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var createdBooking = await db.Bookings.AsNoTracking().SingleAsync(b => b.Id == bookingId);
        Assert.Equal(BookingStatus.Pending, createdBooking.Status);
        Assert.False(string.IsNullOrWhiteSpace(createdBooking.StripeSetupIntentId));
        Assert.False(string.IsNullOrWhiteSpace(createdBooking.StripeCustomerId));

        var paymentMethodId = $"pm_saved_{Guid.NewGuid():N}";
        var setupIntent = new SetupIntent
        {
            Id = createdBooking.StripeSetupIntentId,
            CustomerId = createdBooking.StripeCustomerId,
            PaymentMethodId = paymentMethodId,
            Metadata = new Dictionary<string, string>
            {
                ["kind"] = "direct-booking-setup",
                ["bookingId"] = bookingId.ToString(),
            },
        };

        await handler.HandleEventAsync(new Event
        {
            Type = "setup_intent.succeeded",
            Data = new EventData { Object = setupIntent },
        }, WebhookSource.Connected);

        var booking = await db.Bookings.AsNoTracking().SingleAsync(b => b.Id == bookingId);
        Assert.Equal(BookingStatus.Confirmed, booking.Status);
        Assert.Equal(createdBooking.StripeCustomerId, booking.StripeCustomerId);
        Assert.Equal(paymentMethodId, booking.StripePaymentMethodId);
    }

    [Fact]
    public async Task DeferredBooking_DeadlineChargeWebhook_CompletesPendingPayment()
    {
        var property = await SeedConnectReadyPropertyAsync();
        var client = _factory.CreateClient();
        var response = await PostDirectBookingAsync(
            client,
            BuildPayload(property.Id, paymentOption: PaymentOption.OnCancellationDeadline));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var bookingId = doc.RootElement.GetProperty("bookingId").GetGuid();

        using var scope = _factory.Services.CreateScope();
        var handler = scope.ServiceProvider.GetRequiredService<StripeWebhookHandler>();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var createdBooking = await db.Bookings.AsNoTracking().SingleAsync(b => b.Id == bookingId);
        var setupIntent = new SetupIntent
        {
            Id = createdBooking.StripeSetupIntentId,
            CustomerId = createdBooking.StripeCustomerId,
            PaymentMethodId = $"pm_saved_{Guid.NewGuid():N}",
            Metadata = new Dictionary<string, string>
            {
                ["kind"] = "direct-booking-setup",
                ["bookingId"] = bookingId.ToString(),
            },
        };

        await handler.HandleEventAsync(new Event
        {
            Type = "setup_intent.succeeded",
            Data = new EventData { Object = setupIntent },
        }, WebhookSource.Connected);

        var deadlinePaymentIntentId = $"pi_deadline_{Guid.NewGuid():N}";
        var deadlinePaymentIntent = new PaymentIntent
        {
            Id = deadlinePaymentIntentId,
            Amount = 65000,
            Metadata = new Dictionary<string, string>
            {
                ["kind"] = "direct-booking-deadline-charge",
                ["bookingId"] = bookingId.ToString(),
            },
        };
        var deadlineEvent = new Event
        {
            Type = "payment_intent.succeeded",
            Data = new EventData { Object = deadlinePaymentIntent },
        };

        await handler.HandleEventAsync(deadlineEvent, WebhookSource.Connected);
        await handler.HandleEventAsync(deadlineEvent, WebhookSource.Connected);

        var payments = await db.Payments.AsNoTracking().Where(p => p.BookingId == bookingId).ToListAsync();
        var payment = Assert.Single(payments);
        Assert.Equal(PaymentStatus.Completed, payment.Status);
        Assert.Equal(deadlinePaymentIntentId, payment.TransactionId);
        Assert.Equal(deadlinePaymentIntentId, payment.StripePaymentIntentId);
        Assert.NotNull(payment.ProcessedAt);
    }

    [Fact]
    public async Task Webhook_PaymentOnCancelledDirectBookingWithFreeDates_ReconfirmsBooking()
    {
        // A3-04: the payment used to be marked Completed on the cancelled booking, with no booking and no refund.
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
        var stripe = (FakeStripeService)scope.ServiceProvider.GetRequiredService<IStripeService>();

        await CancelAsync(db, bookingId);

        await handler.HandleEventAsync(DirectBookingPaymentSucceeded(paymentIntentId, bookingId), WebhookSource.Connected);

        var booking = await db.Bookings.AsNoTracking().SingleAsync(b => b.Id == bookingId);
        Assert.Equal(BookingStatus.Confirmed, booking.Status);
        Assert.NotNull(booking.CheckInToken);

        var payment = await db.Payments.AsNoTracking().SingleAsync(p => p.BookingId == bookingId);
        Assert.Equal(PaymentStatus.Completed, payment.Status);
        Assert.DoesNotContain(stripe.RefundRequests, r => r.PaymentIntentId == paymentIntentId);
    }

    [Fact]
    public async Task Webhook_PaymentOnCancelledDirectBookingWhoseDatesWereTaken_RefundsInFullOnConnectedAccount()
    {
        var property = await SeedConnectReadyPropertyAsync();
        var client = _factory.CreateClient();
        var firstResponse = await PostDirectBookingAsync(client, BuildPayload(property.Id));
        Assert.Equal(HttpStatusCode.OK, firstResponse.StatusCode);
        using var firstDoc = JsonDocument.Parse(await firstResponse.Content.ReadAsStringAsync());
        var bookingId = firstDoc.RootElement.GetProperty("bookingId").GetGuid();
        var amount = firstDoc.RootElement.GetProperty("amount").GetDecimal();
        var paymentIntentId = FakeStripeService.LastPaymentIntentId!;

        using var scope = _factory.Services.CreateScope();
        var handler = scope.ServiceProvider.GetRequiredService<StripeWebhookHandler>();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var stripe = (FakeStripeService)scope.ServiceProvider.GetRequiredService<IStripeService>();

        // The first guest's hold is cancelled and a second guest books the same dates before the payment arrives.
        await CancelAsync(db, bookingId);
        var secondResponse = await PostDirectBookingAsync(client, BuildPayload(property.Id));
        Assert.Equal(HttpStatusCode.OK, secondResponse.StatusCode);

        await handler.HandleEventAsync(DirectBookingPaymentSucceeded(paymentIntentId, bookingId), WebhookSource.Connected);

        var booking = await db.Bookings.AsNoTracking().SingleAsync(b => b.Id == bookingId);
        Assert.Equal(BookingStatus.Cancelled, booking.Status);
        var refund = Assert.Single(stripe.RefundRequests, r => r.PaymentIntentId == paymentIntentId);
        Assert.Equal("acct_test_connect_ready", refund.ConnectedAccountId);
        Assert.Equal((long)Math.Round(amount * 100m), refund.AmountCents);
        Assert.Equal($"late-payment-refund:{paymentIntentId}", refund.IdempotencyKey);
        var payment = await db.Payments.AsNoTracking().SingleAsync(p => p.BookingId == bookingId);
        Assert.Equal(PaymentStatus.Refunded, payment.Status);
    }

    private static async Task CancelAsync(AppDbContext db, Guid bookingId)
    {
        var booking = await db.Bookings.SingleAsync(b => b.Id == bookingId);
        booking.Status = BookingStatus.Cancelled;
        booking.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
    }

    private static Event DirectBookingPaymentSucceeded(string paymentIntentId, Guid bookingId) => new()
    {
        Type = "payment_intent.succeeded",
        Data = new EventData
        {
            Object = new PaymentIntent
            {
                Id = paymentIntentId,
                Amount = 65000,
                Metadata = new Dictionary<string, string>
                {
                    ["kind"] = "direct-booking",
                    ["bookingId"] = bookingId.ToString(),
                },
            },
        },
    };

    [Fact]
    public async Task SetupWebhook_DoesNotConfirmCancelledDeferredBooking()
    {
        var property = await SeedConnectReadyPropertyAsync();
        var client = _factory.CreateClient();
        var response = await PostDirectBookingAsync(
            client,
            BuildPayload(property.Id, paymentOption: PaymentOption.OnCancellationDeadline));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var bookingId = doc.RootElement.GetProperty("bookingId").GetGuid();

        using var scope = _factory.Services.CreateScope();
        var handler = scope.ServiceProvider.GetRequiredService<StripeWebhookHandler>();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var cancelledBooking = await db.Bookings.SingleAsync(b => b.Id == bookingId);
        cancelledBooking.Status = BookingStatus.Cancelled;
        cancelledBooking.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();

        var setupIntent = new SetupIntent
        {
            Id = cancelledBooking.StripeSetupIntentId,
            CustomerId = cancelledBooking.StripeCustomerId,
            PaymentMethodId = $"pm_saved_{Guid.NewGuid():N}",
            Metadata = new Dictionary<string, string>
            {
                ["kind"] = "direct-booking-setup",
                ["bookingId"] = bookingId.ToString(),
            },
        };

        await handler.HandleEventAsync(new Event
        {
            Type = "setup_intent.succeeded",
            Data = new EventData { Object = setupIntent },
        }, WebhookSource.Connected);

        var booking = await db.Bookings.AsNoTracking().SingleAsync(b => b.Id == bookingId);
        Assert.Equal(BookingStatus.Cancelled, booking.Status);
        Assert.Null(booking.StripePaymentMethodId);
    }

    [Fact]
    public async Task GetCheckoutOutcome_WithCheckoutToken_ReturnsRealStateWithoutGuestData()
    {
        var property = await SeedConnectReadyPropertyAsync();
        var client = _factory.CreateClient();
        var email = $"outcome.{Guid.NewGuid():N}@example.com";
        var (bookingId, token) = await CreateCheckoutAsync(client, BuildPayload(property.Id, guestEmail: email));

        var response = await PostWithCheckoutTokenAsync(client, bookingId, "outcome", token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        // A3-15: the page shows the real state, never "confirmed" before the payment webhook.
        Assert.Equal("AwaitingPayment", root.GetProperty("state").GetString());
        Assert.Equal("Immediate", root.GetProperty("paymentOption").GetString());
        Assert.Equal("Direct Checkout Villa", root.GetProperty("propertyName").GetString());
        Assert.Equal(JsonValueKind.String, root.GetProperty("expiresAt").ValueKind);
        // Nothing personal about the guest.
        Assert.DoesNotContain(email, body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Mario", body, StringComparison.Ordinal);
        Assert.DoesNotContain("+393331234567", body, StringComparison.Ordinal);

        // Only the hash of the token is stored.
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var stored = await db.Bookings.AsNoTracking().SingleAsync(b => b.Id == bookingId);
        Assert.NotEqual(token, stored.CheckoutTokenHash);
        Assert.True(CheckoutOutcomes.TokenMatches(stored.CheckoutTokenHash, token));
    }

    [Fact]
    public async Task GetCheckoutOutcome_WrongTokenOtherBookingOrUnknownId_Returns404WithTheSameCode()
    {
        var property = await SeedConnectReadyPropertyAsync();
        var client = _factory.CreateClient();
        var first = await CreateCheckoutAsync(client, BuildPayload(property.Id));
        var second = await CreateCheckoutAsync(
            client,
            BuildPayload(property.Id, checkIn: DateTime.UtcNow.Date.AddDays(60), checkOut: DateTime.UtcNow.Date.AddDays(62)));

        // Another guest's token, a made-up token, a guessed id: the same answer, nothing to enumerate.
        foreach (var (bookingId, token) in new[]
                 {
                     (first.BookingId, second.Token),
                     (first.BookingId, "not-the-checkout-token"),
                     (Guid.NewGuid(), first.Token),
                 })
        {
            foreach (var action in new[] { "outcome", "payment-session" })
            {
                var response = await PostWithCheckoutTokenAsync(client, bookingId, action, token);
                Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
                using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                Assert.Equal("checkout_link_invalid", problem.RootElement.GetProperty("code").GetString());
            }
        }

        var missingToken = await PostWithCheckoutTokenAsync(client, first.BookingId, "outcome", null);
        Assert.Equal(HttpStatusCode.BadRequest, missingToken.StatusCode);

        // The booking id alone reveals nothing: the anonymous GET status endpoint is gone.
        var legacy = await client.GetAsync($"/api/public/bookings/{first.BookingId}/status");
        Assert.True(
            legacy.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed,
            $"legacy status endpoint answered {(int)legacy.StatusCode}");
    }

    [Fact]
    public async Task GetCheckoutOutcome_AfterThePaymentWebhook_IsConfirmedAndNothingIsLeftToPay()
    {
        var property = await SeedConnectReadyPropertyAsync();
        var client = _factory.CreateClient();
        var (bookingId, token) = await CreateCheckoutAsync(client, BuildPayload(property.Id));

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var paymentIntentId = (await db.Payments.AsNoTracking().SingleAsync(p => p.BookingId == bookingId)).StripePaymentIntentId!;
            var handler = scope.ServiceProvider.GetRequiredService<StripeWebhookHandler>();
            await handler.HandleEventAsync(DirectBookingPaymentSucceeded(paymentIntentId, bookingId), WebhookSource.Connected);
        }

        Assert.Equal("Confirmed", await ReadOutcomeStateAsync(client, bookingId, token));
        await AssertProblemAsync(
            await PostWithCheckoutTokenAsync(client, bookingId, "payment-session", token),
            HttpStatusCode.Conflict,
            "checkout_payment_not_resumable");
    }

    [Fact]
    public async Task GetCheckoutOutcome_HoldPastItsTtl_IsExpiredAndThePaymentCannotResume()
    {
        var property = await SeedConnectReadyPropertyAsync();
        var client = _factory.CreateClient();
        var (bookingId, token) = await CreateCheckoutAsync(client, BuildPayload(property.Id));

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var booking = await db.Bookings.SingleAsync(b => b.Id == bookingId);
            booking.CreatedAt = DateTime.UtcNow.AddHours(-2);
            await db.SaveChangesAsync();
        }

        Assert.Equal("Expired", await ReadOutcomeStateAsync(client, bookingId, token));
        await AssertProblemAsync(
            await PostWithCheckoutTokenAsync(client, bookingId, "payment-session", token),
            HttpStatusCode.Conflict,
            "checkout_hold_expired");
    }

    [Fact]
    public async Task ResumeCheckoutPayment_ValidHold_ReturnsTheSameIntentWithoutANewBooking()
    {
        // A3-15: back from a redirect method, or after a failed card, the guest pays the same hold instead of booking
        // again and hitting their own hold (409 on the dates).
        var property = await SeedConnectReadyPropertyAsync();
        var client = _factory.CreateClient();
        var (bookingId, token) = await CreateCheckoutAsync(client, BuildPayload(property.Id));

        var response = await PostWithCheckoutTokenAsync(client, bookingId, "payment-session", token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = doc.RootElement;
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var payment = await db.Payments.AsNoTracking().SingleAsync(p => p.BookingId == bookingId);
        Assert.Equal($"{payment.StripePaymentIntentId}_secret_test", root.GetProperty("clientSecret").GetString());
        Assert.Equal(
            "acct_test_connect_ready",
            root.GetProperty("connectedAccountPublishableContext").GetProperty("stripeAccountId").GetString());
        Assert.Equal(1, await db.Bookings.CountAsync(b => b.PropertyId == property.Id));
    }

    [Fact]
    public async Task ResumeCheckoutPayment_IntentAlreadyProcessing_Returns409NotResumable()
    {
        var property = await SeedConnectReadyPropertyAsync();
        var client = _factory.CreateClient();
        var (bookingId, token) = await CreateCheckoutAsync(client, BuildPayload(property.Id));
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var payment = await db.Payments.AsNoTracking().SingleAsync(p => p.BookingId == bookingId);
            FakeStripeService.SetIntentStatus(payment.StripePaymentIntentId!, "processing");
        }

        await AssertProblemAsync(
            await PostWithCheckoutTokenAsync(client, bookingId, "payment-session", token),
            HttpStatusCode.Conflict,
            "checkout_payment_not_resumable");
    }

    [Fact]
    public async Task ResumeCheckoutPayment_DeferredPayment_ReturnsTheSetupIntentSecret()
    {
        var property = await SeedConnectReadyPropertyAsync();
        var client = _factory.CreateClient();
        var (bookingId, token) = await CreateCheckoutAsync(
            client, BuildPayload(property.Id, paymentOption: PaymentOption.OnCancellationDeadline));

        var response = await PostWithCheckoutTokenAsync(client, bookingId, "payment-session", token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var booking = await db.Bookings.AsNoTracking().SingleAsync(b => b.Id == bookingId);
        Assert.Equal($"{booking.StripeSetupIntentId}_secret_test", doc.RootElement.GetProperty("setupIntentClientSecret").GetString());
        Assert.Equal(JsonValueKind.Null, doc.RootElement.GetProperty("clientSecret").ValueKind);
    }

    [Fact]
    public async Task GetCheckoutOutcome_PayAtTheProperty_IsAwaitingTheGuestEmailNotConfirmed()
    {
        var property = await SeedConnectReadyPropertyAsync();
        var client = _factory.CreateClient();
        var (bookingId, token) = await CreateCheckoutAsync(
            client, BuildPayload(property.Id, paymentOption: PaymentOption.OnSite));

        Assert.Equal("AwaitingGuestEmail", await ReadOutcomeStateAsync(client, bookingId, token));
        await AssertProblemAsync(
            await PostWithCheckoutTokenAsync(client, bookingId, "payment-session", token),
            HttpStatusCode.Conflict,
            "checkout_payment_not_resumable");
    }

    [Fact]
    public async Task Quote_ArrivalTomorrowForTenNights_OffersNeitherDeferredPaymentNorFreeCancellation()
    {
        // A3-16: the option used to be offered for stays of more than 7 nights, with a deadline already past.
        var property = await SeedConnectReadyPropertyAsync();
        var client = _factory.CreateClient();
        var tomorrow = TimeProvider.System.TodayInRome().AddDays(1);

        var options = await ReadQuotePaymentOptionsAsync(client, property.Id, tomorrow, tomorrow.AddDays(10));

        Assert.False(options.GetProperty("deferredPaymentAvailable").GetBoolean());
        Assert.Equal(JsonValueKind.Null, options.GetProperty("deferredChargeDate").ValueKind);
        // The guest cannot cancel by themselves (BK-02): no free cancellation is promised.
        Assert.Equal(JsonValueKind.Null, options.GetProperty("freeCancellationUntil").ValueKind);
    }

    [Fact]
    public async Task Quote_ArrivalInThirtyDays_OffersDeferredPaymentChargedSevenDaysBefore()
    {
        var property = await SeedConnectReadyPropertyAsync();
        var client = _factory.CreateClient();
        var checkIn = TimeProvider.System.TodayInRome().AddDays(30);

        var options = await ReadQuotePaymentOptionsAsync(client, property.Id, checkIn, checkIn.AddDays(2));

        Assert.True(options.GetProperty("deferredPaymentAvailable").GetBoolean());
        Assert.Equal(checkIn.AddDays(-7).ToString("yyyy-MM-dd"), options.GetProperty("deferredChargeDate").GetString());
        Assert.Equal(JsonValueKind.Null, options.GetProperty("freeCancellationUntil").ValueKind);
    }

    [Fact]
    public async Task CreateDirectBooking_DeferredPaymentWithArrivalTomorrow_Returns422WithStableCodeAndStoresNothing()
    {
        var property = await SeedConnectReadyPropertyAsync();
        var client = _factory.CreateClient();
        var tomorrow = TimeProvider.System.TodayInRome().AddDays(1);

        var response = await PostDirectBookingAsync(
            client,
            BuildPayload(
                property.Id,
                checkIn: tomorrow,
                checkOut: tomorrow.AddDays(10),
                paymentOption: PaymentOption.OnCancellationDeadline));

        await AssertProblemAsync(response, HttpStatusCode.UnprocessableEntity, "direct_booking_deferred_payment_unavailable");
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.False(await db.Bookings.AnyAsync(b => b.PropertyId == property.Id));
    }

    [Fact]
    public async Task CreateDirectBooking_DatesHeldByAnotherCheckout_Returns409WithStableCode()
    {
        var property = await SeedConnectReadyPropertyAsync();
        var client = _factory.CreateClient();
        await CreateCheckoutAsync(client, BuildPayload(property.Id));

        var response = await PostDirectBookingAsync(client, BuildPayload(property.Id));

        await AssertProblemAsync(response, HttpStatusCode.Conflict, "booking_dates_unavailable");
    }

    private static async Task<(Guid BookingId, string Token)> CreateCheckoutAsync(HttpClient client, object payload)
    {
        var response = await PostDirectBookingAsync(client, payload);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var token = doc.RootElement.GetProperty("checkoutToken").GetString();
        Assert.False(string.IsNullOrWhiteSpace(token));
        return (doc.RootElement.GetProperty("bookingId").GetGuid(), token!);
    }

    private static Task<HttpResponseMessage> PostWithCheckoutTokenAsync(
        HttpClient client, Guid bookingId, string action, string? token) =>
        client.PostAsync(
            $"/api/public/bookings/{bookingId}/{action}",
            new StringContent(JsonSerializer.Serialize(new { token }), Encoding.UTF8, "application/json"));

    private static async Task<string?> ReadOutcomeStateAsync(HttpClient client, Guid bookingId, string token)
    {
        var response = await PostWithCheckoutTokenAsync(client, bookingId, "outcome", token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("state").GetString();
    }

    private static async Task<JsonElement> ReadQuotePaymentOptionsAsync(
        HttpClient client, Guid propertyId, DateTime checkIn, DateTime checkOut)
    {
        var json = JsonSerializer.Serialize(new
        {
            propertyId,
            checkInDate = checkIn.ToString("yyyy-MM-dd"),
            checkOutDate = checkOut.ToString("yyyy-MM-dd"),
            numberOfAdults = 2,
            numberOfChildren = 0,
        });
        var response = await client.PostAsync(
            "/api/public/bookings/quote", new StringContent(json, Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("paymentOptions").Clone();
    }

    private static async Task AssertProblemAsync(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        Assert.Equal(status, response.StatusCode);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(code, problem.RootElement.GetProperty("code").GetString());
        Assert.False(string.IsNullOrWhiteSpace(problem.RootElement.GetProperty("detail").GetString()));
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
        string guestCountry = "IT",
        PaymentOption paymentOption = PaymentOption.Immediate)
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
            paymentOption = paymentOption.ToString(),
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
            CinCode = "IT058091C27G5FFZDZ",
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
            CinCode = "IT058091C27G5FFZDZ",
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
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> IntentStatuses = new();
    private static readonly System.Collections.Concurrent.ConcurrentQueue<(string IntentId, string? AccountId, string IdempotencyKey)> Cancellations = new();

    public static string? LastPaymentIntentId { get; private set; }
    public static string LastClientSecret { get; private set; } = "pi_test_secret_direct";

    /// <summary>Cancellations sent (intent, <c>Stripe-Account</c>, idempotency key), in order.</summary>
    public static IReadOnlyList<(string IntentId, string? AccountId, string IdempotencyKey)> CancelledIntents => Cancellations.ToList();

    // Intent statuses and cancellations are keyed by unique intent ids and never cleared: test classes run in parallel
    // on this shared fake, so a Reset from one class must not wipe what another is asserting on.
    public static void Reset()
    {
        LastPaymentIntentId = null;
        LastClientSecret = "pi_test_secret_direct";
    }

    /// <summary>Stripe status returned for one intent id, whatever the defaults (BK-21: a hold whose guest has paid).</summary>
    public static void SetIntentStatus(string intentId, string status) => IntentStatuses[intentId] = status;

    private static string StatusOf(string intentId, string defaultStatus) =>
        IntentStatuses.TryGetValue(intentId, out var status) ? status : defaultStatus;

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

    /// <summary>Refund requests received, in order (BK-02): account, idempotency key and amount are asserted on them.</summary>
    public System.Collections.Concurrent.ConcurrentQueue<StripeRefundCreateRequest> RefundRequests { get; } = new();

    /// <summary>Status Stripe answers to a refund request (<c>succeeded</c> for cards, <c>pending</c> for some methods).</summary>
    public string RefundStatus { get; set; } = "succeeded";

    /// <summary>
    /// Status of every PaymentIntent read by <see cref="GetPaymentIntentAsync"/>, unless one was set for that intent with
    /// <see cref="SetIntentStatus"/>.
    /// </summary>
    public string PaymentIntentStatus { get; set; } = "requires_payment_method";

    public System.Collections.Concurrent.ConcurrentQueue<(string PaymentIntentId, string? AccountId)> CanceledPaymentIntents { get; } = new();

    public Task<Refund> CreateRefundAsync(StripeRefundCreateRequest request, CancellationToken cancellationToken = default)
    {
        RefundRequests.Enqueue(request);
        return Task.FromResult(new Refund
        {
            Id = $"re_test_{Guid.NewGuid():N}",
            PaymentIntentId = request.PaymentIntentId,
            Amount = request.AmountCents,
            Status = RefundStatus,
            Metadata = new Dictionary<string, string>(request.Metadata),
        });
    }

    public Task<IReadOnlyList<Refund>> ListRefundsAsync(
        string paymentIntentId,
        string? connectedAccountId,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<Refund>>([]);

    public Task<PaymentIntent> GetPaymentIntentAsync(
        string paymentIntentId,
        string? connectedAccountId,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(new PaymentIntent
        {
            Id = paymentIntentId,
            Status = StatusOf(paymentIntentId, PaymentIntentStatus),
            ClientSecret = $"{paymentIntentId}_secret_test",
        });

    public Task<PaymentIntent> CancelPaymentIntentAsync(
        string paymentIntentId,
        string? connectedAccountId,
        string idempotencyKey,
        CancellationToken cancellationToken = default)
    {
        CanceledPaymentIntents.Enqueue((paymentIntentId, connectedAccountId));
        Cancellations.Enqueue((paymentIntentId, connectedAccountId, idempotencyKey));
        return Task.FromResult(new PaymentIntent { Id = paymentIntentId, Status = "canceled" });
    }

    public Task<SetupIntent> GetSetupIntentAsync(
        string setupIntentId,
        string connectedAccountId,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(new SetupIntent
        {
            Id = setupIntentId,
            Status = StatusOf(setupIntentId, "requires_payment_method"),
            ClientSecret = $"{setupIntentId}_secret_test",
        });

    public Task<SetupIntent> CancelSetupIntentAsync(
        string setupIntentId,
        string connectedAccountId,
        string idempotencyKey,
        CancellationToken cancellationToken = default)
    {
        Cancellations.Enqueue((setupIntentId, connectedAccountId, idempotencyKey));
        return Task.FromResult(new SetupIntent { Id = setupIntentId, Status = "canceled" });
    }

    public Task DetachPaymentMethodAsync(
        string paymentMethodId,
        string connectedAccountId,
        CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    public Task<SetupIntent> CreateConnectedAccountSetupIntentAsync(
        string connectedAccountId,
        Dictionary<string, string> metadata,
        string? customerEmail = null,
        string? customerName = null)
    {
        return Task.FromResult(new SetupIntent
        {
            Id = $"seti_test_{Guid.NewGuid():N}",
            CustomerId = $"cus_test_{Guid.NewGuid():N}",
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
        Dictionary<string, string> metadata,
        string idempotencyKey,
        CancellationToken cancellationToken = default)
    {
        LastPaymentIntentId = $"pi_test_{Guid.NewGuid():N}";
        return Task.FromResult(new PaymentIntent
        {
            Id = LastPaymentIntentId,
            Amount = amountCents,
            Currency = currency,
            Metadata = metadata,
            Status = StatusOf(LastPaymentIntentId, "succeeded"),
        });
    }

    public Task<PaymentIntent> ConfirmPaymentIntentOffSessionAsync(
        string paymentIntentId,
        string connectedAccountId,
        string paymentMethodId,
        string idempotencyKey,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(new PaymentIntent { Id = paymentIntentId, Status = StatusOf(paymentIntentId, "succeeded") });

    public Task<IReadOnlyList<PaymentIntent>> ListCustomerPaymentIntentsAsync(
        string customerId,
        string connectedAccountId,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<PaymentIntent>>([]);
}
