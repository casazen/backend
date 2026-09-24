using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Repositories;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.External;
using Casazen.Infrastructure.Repositories;
using Casazen.Infrastructure.Services;
using Casazen.Tests.Integration.Postgres;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Npgsql;
using Stripe;
using PlanTier = Casazen.Core.Entities.Enums.PlanTier;
using Xunit;

namespace Casazen.Tests.Integration;

/// <summary>
/// Stripe webhook idempotency on real PostgreSQL (A1-09, A3-03), for the platform and the Connect endpoint: every
/// worker runs on its own <see cref="AppDbContext"/> (connection), as Hangfire jobs do. Also the fail-closed
/// subscription states of the platform billing (A1-11) with the real entitlement rules.
/// </summary>
public class StripeWebhookIdempotencyPostgresTests : IAsyncLifetime
{
    private const string ScalePrice = "price_test_scale";

    private static readonly IConfiguration Config = new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Billing:Prices:Starter"] = "price_test_starter",
            ["Billing:Prices:Pro"] = "price_test_pro",
            ["Billing:Prices:Scale"] = ScalePrice,
            ["Billing:PastDueGraceDays"] = "7",
        })
        .Build();

    private PostgresTestDatabase? _database;

    public async Task InitializeAsync()
    {
        _database = await PostgresTestDatabase.CreateAsync();
        await using var db = NewContext();
        await db.Database.MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        if (_database is not null)
            await _database.DisposeAsync();
    }

    [PostgresFact]
    public async Task HandleEventAsync_ConnectEventDeliveredTwice_AppliesEffectOnce() =>
        await AssertSequentialDuplicateAppliedOnceAsync(WebhookSource.Connected);

    [PostgresFact]
    public async Task HandleEventAsync_PlatformEventDeliveredTwice_AppliesEffectOnce() =>
        await AssertSequentialDuplicateAppliedOnceAsync(WebhookSource.Platform);

    [PostgresFact]
    public async Task HandleEventAsync_ConnectEventProcessedInParallel_AppliesEffectOnce() =>
        await AssertParallelDuplicateAppliedOnceAsync(WebhookSource.Connected);

    [PostgresFact]
    public async Task HandleEventAsync_PlatformEventProcessedInParallel_AppliesEffectOnce() =>
        await AssertParallelDuplicateAppliedOnceAsync(WebhookSource.Platform);

    [PostgresFact]
    public async Task HandleEventAsync_ManyParallelDeliveries_AppliesEffectOnce()
    {
        var seed = await SeedDirectBookingAsync();
        var stripeEvent = StripeTestEvents.PaymentIntentSucceeded(seed.PaymentIntentId, "direct-booking");
        var contexts = Enumerable.Range(0, 5).Select(_ => NewContext()).ToList();
        try
        {
            var repositories = contexts.Select(db => new InstrumentedPaymentRepository(new PaymentRepository(db))).ToList();
            await Task.WhenAll(contexts.Select((db, i) => Task.Run(() =>
                CreateHandler(db, repositories[i]).HandleEventAsync(stripeEvent, WebhookSource.Connected))));

            Assert.Equal(1, repositories.Sum(r => r.Lookups));
        }
        finally
        {
            foreach (var db in contexts)
                await db.DisposeAsync();
        }

        await AssertDirectBookingPaidAsync(seed, stripeEvent.Id);
    }

    [PostgresFact]
    public async Task HandleEventAsync_FirstWorkerFailsWhileDuplicateWaits_DuplicateProcessesEvent()
    {
        var seed = await SeedDirectBookingAsync();
        var stripeEvent = StripeTestEvents.PaymentIntentSucceeded(seed.PaymentIntentId, "direct-booking");
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var firstDb = NewContext();
        await using var secondDb = NewContext();
        var firstRepository = new InstrumentedPaymentRepository(new PaymentRepository(firstDb))
        {
            Gate = releaseFirst.Task,
            FailOnLookup = new TimeoutException("Simulated database timeout after the claim"),
        };
        var secondRepository = new InstrumentedPaymentRepository(new PaymentRepository(secondDb));

        var first = Task.Run(() => CreateHandler(firstDb, firstRepository).HandleEventAsync(stripeEvent, WebhookSource.Connected));
        await firstRepository.Entered.Task.WaitAsync(TimeSpan.FromSeconds(30));
        var second = Task.Run(() => CreateHandler(secondDb, secondRepository).HandleEventAsync(stripeEvent, WebhookSource.Connected));
        await WaitForClaimWaiterAsync();

        releaseFirst.SetResult();
        await Assert.ThrowsAsync<TimeoutException>(() => first);
        await second;

        // The failed claim was rolled back: the waiting duplicate claimed the event and processed it.
        Assert.Equal(1, secondRepository.Lookups);
        await AssertDirectBookingPaidAsync(seed, stripeEvent.Id);
    }

    [PostgresFact]
    public async Task HandleEventAsync_FailureAfterPartialWrite_RollsBackAndRetrySucceeds()
    {
        var seed = await SeedDirectBookingAsync();
        var stripeEvent = StripeTestEvents.PaymentIntentSucceeded(seed.PaymentIntentId, "direct-booking");

        await using (var failingDb = NewContext())
        {
            var failing = new InstrumentedPaymentRepository(new PaymentRepository(failingDb))
            {
                FailAfterUpdate = new TimeoutException("Simulated database timeout after the payment update"),
            };
            await Assert.ThrowsAsync<TimeoutException>(() =>
                CreateHandler(failingDb, failing).HandleEventAsync(stripeEvent, WebhookSource.Connected));
        }

        await using (var check = NewContext())
        {
            // A3-03: nothing half-done and no claim left, so the retry is not skipped.
            Assert.Equal(PaymentStatus.Pending, (await check.Payments.SingleAsync(p => p.Id == seed.PaymentId)).Status);
            Assert.Equal(BookingStatus.Pending, (await check.Bookings.SingleAsync(b => b.Id == seed.BookingId)).Status);
            Assert.False(await check.ProcessedStripeEvents.AnyAsync(e => e.EventId == stripeEvent.Id));
        }

        await using (var retryDb = NewContext())
            await CreateHandler(retryDb).HandleEventAsync(stripeEvent, WebhookSource.Connected);

        await AssertDirectBookingPaidAsync(seed, stripeEvent.Id);
    }

    [PostgresFact]
    public async Task HandleEventAsync_PlatformInvoiceFailureRolledBack_RetryMarksPastDue()
    {
        var org = await SeedOrgAsync(SubscriptionStatus.Active, PlanTier.Pro);
        var stripeEvent = StripeTestEvents.Invoice(
            "invoice.payment_failed", org.SubscriptionId!, org.StripeCustomerId!, "subscription_cycle");

        await using (var failingDb = NewContext())
        {
            var entitlement = new Mock<IEntitlementService>();
            entitlement.Setup(e => e.SyncFromSubscriptionAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
                .ThrowsAsync(new TimeoutException("Simulated database timeout after the org update"));
            await Assert.ThrowsAsync<TimeoutException>(() =>
                CreateHandler(failingDb, entitlement: entitlement.Object).HandleEventAsync(stripeEvent, WebhookSource.Platform));
        }

        await using (var check = NewContext())
        {
            // A1-09: the org update and the claim are rolled back together.
            var stored = await check.Orgs.SingleAsync(o => o.Id == org.Id);
            Assert.Equal(SubscriptionStatus.Active, stored.SubscriptionStatus);
            Assert.Null(stored.PastDueSince);
            Assert.False(await check.ProcessedStripeEvents.AnyAsync(e => e.EventId == stripeEvent.Id));
        }

        await using (var retryDb = NewContext())
            await CreateHandler(retryDb).HandleEventAsync(stripeEvent, WebhookSource.Platform);

        await using var after = NewContext();
        var updated = await after.Orgs.SingleAsync(o => o.Id == org.Id);
        Assert.Equal(SubscriptionStatus.PastDue, updated.SubscriptionStatus);
        Assert.NotNull(updated.PastDueSince);
        var claim = await after.ProcessedStripeEvents.SingleAsync(e => e.EventId == stripeEvent.Id);
        Assert.Equal(WebhookSource.Platform, claim.Source);
    }

    [PostgresFact]
    public async Task HandleEventAsync_SubscriptionIncomplete_GrantsNoPaidPlan()
    {
        var org = await SeedOrgAsync(SubscriptionStatus.None, PlanTier.Starter, withSubscription: false);
        var subscriptionId = $"sub_test_{Guid.NewGuid():N}";

        await HandleAsync(StripeTestEvents.Subscription(
            "customer.subscription.created", subscriptionId, "incomplete", org.Id, org.StripeCustomerId!, ScalePrice));
        // The first payment fails: not a renewal, no past-due grace.
        await HandleAsync(StripeTestEvents.Invoice(
            "invoice.payment_failed", subscriptionId, org.StripeCustomerId!, "subscription_create"));

        await using (var db = NewContext())
        {
            var stored = await db.Orgs.SingleAsync(o => o.Id == org.Id);
            Assert.Equal(SubscriptionStatus.Incomplete, stored.SubscriptionStatus);
            Assert.Equal(PlanTier.Starter, stored.PlanTier);
            var entitlement = new EntitlementService(db, Config);
            Assert.Equal(PlanTier.Starter.ToString(), (await entitlement.GetEntitlementAsync(org.Id)).PlanTier);
            Assert.False(await entitlement.CanUseCustomDomainAsync(org.Id));
            Assert.True(BillingSubscriptionPolicy.BlocksNewCheckout(stored));
        }

        await HandleAsync(StripeTestEvents.Subscription(
            "customer.subscription.updated", subscriptionId, "incomplete_expired", org.Id, org.StripeCustomerId!, ScalePrice));

        await using var expired = NewContext();
        var final = await expired.Orgs.SingleAsync(o => o.Id == org.Id);
        Assert.Equal(SubscriptionStatus.Canceled, final.SubscriptionStatus);
        Assert.Equal(PlanTier.Starter, final.PlanTier);
        Assert.False(BillingSubscriptionPolicy.BlocksNewCheckout(final));
    }

    [PostgresFact]
    public async Task HandleEventAsync_IncompleteCreatedProcessedAfterActivation_KeepsPaidPlan()
    {
        var org = await SeedOrgAsync(SubscriptionStatus.None, PlanTier.Starter, withSubscription: false);
        var subscriptionId = $"sub_test_{Guid.NewGuid():N}";

        // Hangfire ran the "updated → active" job before the "created → incomplete" one.
        await HandleAsync(StripeTestEvents.Subscription(
            "customer.subscription.updated", subscriptionId, "active", org.Id, org.StripeCustomerId!, ScalePrice));
        await HandleAsync(StripeTestEvents.Subscription(
            "customer.subscription.created", subscriptionId, "incomplete", org.Id, org.StripeCustomerId!, ScalePrice));

        await using var db = NewContext();
        var stored = await db.Orgs.SingleAsync(o => o.Id == org.Id);
        Assert.Equal(SubscriptionStatus.Active, stored.SubscriptionStatus);
        Assert.Equal(PlanTier.Scale, stored.PlanTier);
        Assert.Equal(PlanTier.Scale.ToString(), (await new EntitlementService(db, Config).GetEntitlementAsync(org.Id)).PlanTier);
    }

    [PostgresFact]
    public async Task HandleEventAsync_SubscriptionUnpaid_RevokesPaidPlan()
    {
        var org = await SeedOrgAsync(SubscriptionStatus.Active, PlanTier.Scale);

        await HandleAsync(StripeTestEvents.Subscription(
            "customer.subscription.updated", org.SubscriptionId!, "unpaid", org.Id, org.StripeCustomerId!, ScalePrice));

        await using var db = NewContext();
        var stored = await db.Orgs.SingleAsync(o => o.Id == org.Id);
        Assert.Equal(SubscriptionStatus.Unpaid, stored.SubscriptionStatus);
        Assert.Equal(PlanTier.Starter.ToString(), (await new EntitlementService(db, Config).GetEntitlementAsync(org.Id)).PlanTier);
        Assert.True(BillingSubscriptionPolicy.BlocksNewCheckout(stored));
    }

    private async Task AssertSequentialDuplicateAppliedOnceAsync(WebhookSource source)
    {
        var seed = await SeedDirectBookingAsync();
        var stripeEvent = EventFor(seed, source);

        await using (var firstDb = NewContext())
            await CreateHandler(firstDb).HandleEventAsync(stripeEvent, source);

        await using (var duplicateDb = NewContext())
        {
            var duplicate = new InstrumentedPaymentRepository(new PaymentRepository(duplicateDb));
            await CreateHandler(duplicateDb, duplicate).HandleEventAsync(stripeEvent, source);
            Assert.Equal(0, duplicate.Lookups);
        }

        await AssertPaymentCompletedOnceAsync(seed, stripeEvent.Id, source);
    }

    private async Task AssertParallelDuplicateAppliedOnceAsync(WebhookSource source)
    {
        var seed = await SeedDirectBookingAsync();
        var stripeEvent = EventFor(seed, source);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var firstDb = NewContext();
        await using var secondDb = NewContext();
        var firstRepository = new InstrumentedPaymentRepository(new PaymentRepository(firstDb)) { Gate = releaseFirst.Task };
        var secondRepository = new InstrumentedPaymentRepository(new PaymentRepository(secondDb));

        // Worker 1 has claimed the event (uncommitted) and is still processing it when worker 2 starts.
        var first = Task.Run(() => CreateHandler(firstDb, firstRepository).HandleEventAsync(stripeEvent, source));
        await firstRepository.Entered.Task.WaitAsync(TimeSpan.FromSeconds(30));
        var second = Task.Run(() => CreateHandler(secondDb, secondRepository).HandleEventAsync(stripeEvent, source));
        await WaitForClaimWaiterAsync();

        releaseFirst.SetResult();
        await Task.WhenAll(first, second);

        Assert.Equal(1, firstRepository.Lookups);
        Assert.Equal(0, secondRepository.Lookups);
        await AssertPaymentCompletedOnceAsync(seed, stripeEvent.Id, source);
    }

    private static Event EventFor(DirectBookingSeed seed, WebhookSource source) =>
        StripeTestEvents.PaymentIntentSucceeded(
            seed.PaymentIntentId,
            source == WebhookSource.Connected ? "direct-booking" : null);

    private async Task AssertPaymentCompletedOnceAsync(DirectBookingSeed seed, string eventId, WebhookSource source)
    {
        if (source == WebhookSource.Connected)
        {
            await AssertDirectBookingPaidAsync(seed, eventId);
            return;
        }

        await using var db = NewContext();
        Assert.Equal(PaymentStatus.Completed, (await db.Payments.SingleAsync(p => p.Id == seed.PaymentId)).Status);
        var claim = await db.ProcessedStripeEvents.SingleAsync(e => e.EventId == eventId);
        Assert.Equal(WebhookSource.Platform, claim.Source);
    }

    private async Task AssertDirectBookingPaidAsync(DirectBookingSeed seed, string eventId)
    {
        await using var db = NewContext();
        Assert.Equal(PaymentStatus.Completed, (await db.Payments.SingleAsync(p => p.Id == seed.PaymentId)).Status);
        Assert.Equal(1, await db.Payments.CountAsync(p => p.TransactionId == seed.PaymentIntentId));
        Assert.Equal(BookingStatus.Confirmed, (await db.Bookings.SingleAsync(b => b.Id == seed.BookingId)).Status);
        var claim = await db.ProcessedStripeEvents.SingleAsync(e => e.EventId == eventId);
        Assert.Equal(WebhookSource.Connected, claim.Source);
    }

    /// <summary>Waits until a session of this database blocks on the uncommitted claim of another transaction.</summary>
    private async Task WaitForClaimWaiterAsync()
    {
        await using var connection = new NpgsqlConnection(_database!.ConnectionString);
        await connection.OpenAsync();
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            await using var command = new NpgsqlCommand(
                "SELECT count(*) FROM pg_stat_activity WHERE datname = current_database() AND wait_event_type = 'Lock' AND wait_event = 'transactionid'",
                connection);
            if ((long)(await command.ExecuteScalarAsync())! > 0)
                return;

            await Task.Delay(25);
        }

        throw new TimeoutException("The duplicate worker never waited on the claim of the first one.");
    }

    private async Task HandleAsync(Event stripeEvent)
    {
        await using var db = NewContext();
        await CreateHandler(db).HandleEventAsync(stripeEvent, WebhookSource.Platform);
    }

    private AppDbContext NewContext() => _database!.CreateContext();

    private static StripeWebhookHandler CreateHandler(
        AppDbContext db,
        IPaymentRepository? paymentRepository = null,
        IEntitlementService? entitlement = null) =>
        new(
            paymentRepository ?? new PaymentRepository(db),
            new BookingRepository(db),
            Mock.Of<IConnectOnboardingService>(),
            db,
            new FakeStripeBillingService(Config),
            entitlement ?? new EntitlementService(db, Config),
            new VatCalculationService(),
            Mock.Of<IOssRevenueTracker>(),
            Mock.Of<ISdiEInvoiceService>(),
            Mock.Of<IRentBillingService>(),
            Mock.Of<IPaymentRefundService>(),
            NullLogger<StripeWebhookHandler>.Instance);

    private async Task<OrgEntity> SeedOrgAsync(SubscriptionStatus status, PlanTier tier, bool withSubscription = true)
    {
        var org = new OrgEntity
        {
            Name = "Billing Org",
            Slug = $"billing-{Guid.NewGuid():N}",
            DisplayName = "Billing Org",
            ContactEmail = "billing@example.com",
            PlanTier = tier,
            StripeCustomerId = $"cus_test_{Guid.NewGuid():N}",
            SubscriptionId = withSubscription ? $"sub_test_{Guid.NewGuid():N}" : null,
            SubscriptionStatus = status,
            IsActive = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };

        await using var db = NewContext();
        db.Orgs.Add(org);
        await db.SaveChangesAsync();
        return org;
    }

    private async Task<DirectBookingSeed> SeedDirectBookingAsync()
    {
        var org = new OrgEntity
        {
            Name = "Webhook Org",
            Slug = $"webhook-{Guid.NewGuid():N}",
            DisplayName = "Webhook Org",
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
            Address = $"Via Webhook {Guid.NewGuid():N}",
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
            OrgId = org.Id,
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

        await using var db = NewContext();
        db.Orgs.Add(org);
        db.Properties.Add(property);
        db.Guests.Add(guest);
        db.Bookings.Add(booking);
        db.Payments.Add(payment);
        await db.SaveChangesAsync();

        return new DirectBookingSeed(booking.Id, payment.Id, paymentIntentId);
    }

    private sealed record DirectBookingSeed(Guid BookingId, Guid PaymentId, string PaymentIntentId);

    /// <summary>
    /// Counts the business lookups of a worker (the first repository call of every payment_intent handler) and can
    /// hold the worker inside its transaction or make it fail there.
    /// </summary>
    private sealed class InstrumentedPaymentRepository(IPaymentRepository inner) : IPaymentRepository
    {
        private int _lookups;

        public int Lookups => _lookups;

        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task? Gate { get; init; }

        public Exception? FailOnLookup { get; init; }

        public Exception? FailAfterUpdate { get; init; }

        public async Task<Payment?> GetByTransactionIdAsync(string transactionId)
        {
            Interlocked.Increment(ref _lookups);
            Entered.TrySetResult();
            if (Gate is not null)
                await Gate;
            if (FailOnLookup is not null)
                throw FailOnLookup;
            return await inner.GetByTransactionIdAsync(transactionId);
        }

        public async Task<Payment> UpdateAsync(Payment payment)
        {
            var updated = await inner.UpdateAsync(payment);
            if (FailAfterUpdate is not null)
                throw FailAfterUpdate;
            return updated;
        }

        public Task<Payment?> GetByIdAsync(Guid id) => inner.GetByIdAsync(id);

        public Task<IEnumerable<Payment>> GetByBookingAsync(Guid bookingId) => inner.GetByBookingAsync(bookingId);

        public Task<IEnumerable<Payment>> GetByPropertyAsync(Guid propertyId) => inner.GetByPropertyAsync(propertyId);

        public Task<IEnumerable<Payment>> GetByScopeAsync(HostScope scope) => inner.GetByScopeAsync(scope);

        public Task<Payment> AddAsync(Payment payment) => inner.AddAsync(payment);

        public Task<decimal> GetTotalRevenueAsync(Guid propertyId, DateTime startDate, DateTime endDate) =>
            inner.GetTotalRevenueAsync(propertyId, startDate, endDate);
    }
}
