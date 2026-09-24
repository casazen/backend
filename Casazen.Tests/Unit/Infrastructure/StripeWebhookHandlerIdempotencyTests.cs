using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Repositories;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.External;
using Casazen.Infrastructure.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Stripe;
using Casazen.Tests.Integration;
using Xunit;

namespace Casazen.Tests.Unit.Infrastructure;

public class StripeWebhookHandlerIdempotencyTests
{
    [Fact]
    public async Task HandleEventAsync_WhenBusinessHandlerThrows_DoesNotPersistProcessedMarker()
    {
        await using var db = NewDb();
        var (_, _, _, paymentIntentId) = await SeedDirectBookingPaymentAsync(db);
        var handler = CreateHandler(db, new ThrowingPaymentRepository());

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            handler.HandleEventAsync(DirectBookingSucceededEvent("evt_retryable", paymentIntentId), WebhookSource.Connected));

        Assert.False(await db.ProcessedStripeEvents.AnyAsync(e => e.EventId == "evt_retryable"));
    }

    [Fact]
    public async Task HandleEventAsync_AfterFailedAttempt_CanRetryAndMarkProcessed()
    {
        await using var db = NewDb();
        var (bookingId, _, _, paymentIntentId) = await SeedDirectBookingPaymentAsync(db);

        var failingHandler = CreateHandler(db, new ThrowingPaymentRepository());
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            failingHandler.HandleEventAsync(DirectBookingSucceededEvent("evt_retryable", paymentIntentId), WebhookSource.Connected));

        var retryHandler = CreateHandler(db);
        await retryHandler.HandleEventAsync(DirectBookingSucceededEvent("evt_retryable", paymentIntentId), WebhookSource.Connected);

        var booking = await db.Bookings.AsNoTracking().SingleAsync(b => b.Id == bookingId);
        var payment = await db.Payments.AsNoTracking().SingleAsync(p => p.TransactionId == paymentIntentId);
        Assert.Equal(BookingStatus.Confirmed, booking.Status);
        Assert.Equal(PaymentStatus.Completed, payment.Status);
        Assert.True(await db.ProcessedStripeEvents.AnyAsync(e => e.EventId == "evt_retryable"));
    }

    [Fact]
    public async Task HandleEventAsync_WhenEventAlreadyProcessed_DoesNotInvokeBusinessHandler()
    {
        await using var db = NewDb();
        var (_, _, _, paymentIntentId) = await SeedDirectBookingPaymentAsync(db);
        db.ProcessedStripeEvents.Add(new ProcessedStripeEvent
        {
            EventId = "evt_duplicate",
            EventType = "payment_intent.succeeded",
            Source = WebhookSource.Connected,
            ProcessedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();

        var handler = CreateHandler(db, new ThrowingPaymentRepository());

        await handler.HandleEventAsync(DirectBookingSucceededEvent("evt_duplicate", paymentIntentId), WebhookSource.Connected);
    }

    private static AppDbContext NewDb()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"stripe-webhook-idempotency-{Guid.NewGuid()}")
            .Options;
        return new AppDbContext(options);
    }

    private static async Task<(Guid BookingId, Guid OrgId, Guid PropertyId, string PaymentIntentId)> SeedDirectBookingPaymentAsync(AppDbContext db)
    {
        var org = new OrgEntity
        {
            Name = "Stripe Webhook Org",
            Slug = $"stripe-webhook-{Guid.NewGuid():N}",
            DisplayName = "Stripe Webhook Org",
            ContactEmail = "host@example.com",
            IsActive = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        var property = new Property
        {
            OrgId = org.Id,
            OwnerId = "owner|stripe-webhook",
            Name = "Webhook Villa",
            Address = "Via Webhook 1",
            City = "Rome",
            PostalCode = "00100",
            MaxGuests = 4,
            NightlyRate = 100m,
            IsActive = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        var guest = new Guest
        {
            FirstName = "Retry",
            LastName = "Guest",
            Email = $"retry.{Guid.NewGuid():N}@example.com",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        var booking = new Booking
        {
            OrgId = org.Id,
            PropertyId = property.Id,
            GuestId = guest.Id,
            CheckInDate = DateTime.UtcNow.Date.AddDays(30),
            CheckOutDate = DateTime.UtcNow.Date.AddDays(34),
            NumberOfGuests = 2,
            Status = BookingStatus.Pending,
            Source = BookingSource.Direct,
            TotalPrice = 500m,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        var paymentIntentId = $"pi_{Guid.NewGuid():N}";
        var payment = new Payment
        {
            OrgId = org.Id,
            BookingId = booking.Id,
            Amount = booking.TotalPrice,
            Status = PaymentStatus.Pending,
            Method = Casazen.Core.Entities.PaymentMethod.CreditCard,
            TransactionId = paymentIntentId,
            StripePaymentIntentId = paymentIntentId,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };

        db.Orgs.Add(org);
        db.Properties.Add(property);
        db.Guests.Add(guest);
        db.Bookings.Add(booking);
        db.Payments.Add(payment);
        await db.SaveChangesAsync();

        return (booking.Id, org.Id, property.Id, paymentIntentId);
    }

    private static StripeWebhookHandler CreateHandler(AppDbContext db, IPaymentRepository? paymentRepository = null) =>
        new(
            paymentRepository ?? new PaymentRepository(db),
            new BookingRepository(db),
            Mock.Of<IConnectOnboardingService>(),
            db,
            Mock.Of<IStripeBillingService>(),
            Mock.Of<IEntitlementService>(),
            Mock.Of<IVatCalculationService>(),
            Mock.Of<IOssRevenueTracker>(),
            Mock.Of<ISdiEInvoiceService>(),
            Mock.Of<IRentBillingService>(),
            Mock.Of<IPaymentRefundService>(),
            TestCheckoutPaymentSettlement.Create(db, paymentRepository),
            TestDeferredCharges.Create(db),
            NullLogger<StripeWebhookHandler>.Instance);

    private static Event DirectBookingSucceededEvent(string eventId, string paymentIntentId) =>
        new()
        {
            Id = eventId,
            Type = "payment_intent.succeeded",
            Data = new EventData
            {
                Object = new PaymentIntent
                {
                    Id = paymentIntentId,
                    Metadata = new Dictionary<string, string>
                    {
                        ["kind"] = "direct-booking",
                    },
                },
            },
        };

    private sealed class ThrowingPaymentRepository : IPaymentRepository
    {
        public Task<Payment?> GetByIdAsync(Guid id) => throw new NotImplementedException();

        public Task<Payment?> GetByTransactionIdAsync(string transactionId) =>
            throw new InvalidOperationException("Simulated repository failure after Stripe event claim.");

        public Task<IEnumerable<Payment>> GetByBookingAsync(Guid bookingId) => throw new NotImplementedException();

        public Task<IEnumerable<Payment>> GetByPropertyAsync(Guid propertyId) => throw new NotImplementedException();

        public Task<IEnumerable<Payment>> GetByScopeAsync(HostScope scope) => throw new NotImplementedException();

        public Task<Payment> AddAsync(Payment payment) => throw new NotImplementedException();

        public Task<Payment> UpdateAsync(Payment payment) => throw new NotImplementedException();

        public Task<decimal> GetTotalRevenueAsync(Guid propertyId, DateTime startDate, DateTime endDate) =>
            throw new NotImplementedException();
    }
}
