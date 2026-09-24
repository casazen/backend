using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Email;
using Casazen.Infrastructure.Email.Templates;
using Casazen.Infrastructure.External;
using Casazen.Infrastructure.Repositories;
using Casazen.Infrastructure.Services;
using Casazen.Tests.Integration.Postgres;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Stripe;
using Xunit;
using PaymentMethod = Casazen.Core.Entities.PaymentMethod;

namespace Casazen.Tests.Integration;

/// <summary>
/// BK-02 (A3-05) on real PostgreSQL: refunds of direct charges reach CasaZen through the <b>Connect</b> webhook
/// endpoint (<c>charge.refunded</c>, <c>refund.*</c>) and are applied only for the account the payment was made on; the
/// payment becomes Refunded / PartiallyRefunded only for refunds Stripe reports succeeded; a double click on "cancel"
/// refunds once (advisory locks).
/// </summary>
public class StripeRefundPostgresTests : IAsyncLifetime
{
    private const string Account = "acct_refund_host";

    private static readonly IConfiguration Config = new ConfigurationBuilder().AddInMemoryCollection().Build();

    private readonly Mock<IStripeService> _stripe = new();
    private readonly Mock<IEmailQueue> _emails = new();
    private readonly List<string?> _emailRecipients = [];
    private PostgresTestDatabase? _database;

    public async Task InitializeAsync()
    {
        _database = await PostgresTestDatabase.CreateAsync();
        await using var db = NewContext();
        await db.Database.MigrateAsync();

        _emails
            .Setup(q => q.Enqueue(It.IsAny<string?>(), It.IsAny<EmailContent>(), EmailTemplates.Names.GuestRefundConfirmed))
            .Callback<string?, EmailContent, string>((to, _, _) =>
            {
                lock (_emailRecipients)
                    _emailRecipients.Add(to);
            })
            .Returns(true);
    }

    public async Task DisposeAsync()
    {
        if (_database is not null)
            await _database.DisposeAsync();
    }

    [PostgresFact]
    public async Task HandleEventAsync_ConnectChargeRefunded_AppliesSucceededRefundsListedOnConnectedAccount()
    {
        var seed = await SeedPaidBookingAsync(500m);
        _stripe
            .Setup(s => s.ListRefundsAsync(seed.PaymentIntentId, Account, It.IsAny<CancellationToken>()))
            .ReturnsAsync([new Refund { Id = "re_dashboard", PaymentIntentId = seed.PaymentIntentId, Amount = 50_000, Status = "succeeded" }]);
        var chargeRefunded = ChargeRefunded(seed.PaymentIntentId, Account);

        await HandleAsync(chargeRefunded, WebhookSource.Connected);
        await HandleAsync(chargeRefunded, WebhookSource.Connected); // duplicate delivery

        await using var db = NewContext();
        var payment = await db.Payments.SingleAsync(p => p.Id == seed.PaymentId);
        Assert.Equal(PaymentStatus.Refunded, payment.Status);
        Assert.Equal(500m, payment.RefundedAmount);
        var refund = await db.PaymentRefunds.SingleAsync(r => r.PaymentId == seed.PaymentId);
        Assert.Equal(PaymentRefundStatus.Succeeded, refund.Status);
        Assert.Equal(PaymentRefundOrigin.Stripe, refund.Origin);
        Assert.Equal("re_dashboard", refund.StripeRefundId);
        Assert.NotNull(refund.GuestNotifiedAt);
        Assert.Equal(WebhookSource.Connected, (await db.ProcessedStripeEvents.SingleAsync(e => e.EventId == chargeRefunded.Id)).Source);
        _stripe.Verify(s => s.ListRefundsAsync(seed.PaymentIntentId, Account, It.IsAny<CancellationToken>()), Times.Once);
        Assert.Equal([seed.GuestEmail], _emailRecipients);
    }

    [PostgresFact]
    public async Task HandleEventAsync_ConnectRefundPendingThenSucceeded_RefundedOnlyAfterConfirmation()
    {
        var seed = await SeedPaidBookingAsync(500m);

        await HandleAsync(RefundEvent("refund.created", seed.PaymentIntentId, "re_bank", 20_000, "pending", Account), WebhookSource.Connected);

        await using (var db = NewContext())
        {
            var payment = await db.Payments.SingleAsync(p => p.Id == seed.PaymentId);
            Assert.Equal(PaymentStatus.Completed, payment.Status);
            Assert.Equal(0m, payment.RefundedAmount);
            Assert.Equal(PaymentRefundStatus.Pending, (await db.PaymentRefunds.SingleAsync()).Status);
            Assert.Empty(_emailRecipients);
        }

        await HandleAsync(RefundEvent("refund.updated", seed.PaymentIntentId, "re_bank", 20_000, "succeeded", Account), WebhookSource.Connected);

        await using (var db = NewContext())
        {
            var payment = await db.Payments.SingleAsync(p => p.Id == seed.PaymentId);
            Assert.Equal(PaymentStatus.PartiallyRefunded, payment.Status);
            Assert.Equal(200m, payment.RefundedAmount);
            Assert.Equal(1, await db.PaymentRefunds.CountAsync());
        }

        Assert.Equal([seed.GuestEmail], _emailRecipients);
    }

    [PostgresFact]
    public async Task HandleEventAsync_RefundOfAnotherConnectedAccount_LeavesPaymentUnchanged()
    {
        var seed = await SeedPaidBookingAsync(500m);

        await HandleAsync(
            RefundEvent("refund.updated", seed.PaymentIntentId, "re_foreign", 50_000, "succeeded", "acct_someone_else"),
            WebhookSource.Connected);

        await using var db = NewContext();
        Assert.Equal(PaymentStatus.Completed, (await db.Payments.SingleAsync(p => p.Id == seed.PaymentId)).Status);
        Assert.False(await db.PaymentRefunds.AnyAsync());
        Assert.Empty(_emailRecipients);
    }

    [PostgresFact]
    public async Task HandleEventAsync_RefundWhoseResponseWasLost_LinksCasaZenRefundThroughMetadata()
    {
        var seed = await SeedPaidBookingAsync(500m);
        var pending = new PaymentRefund
        {
            PaymentId = seed.PaymentId,
            OrgId = seed.OrgId,
            Amount = 120m,
            Status = PaymentRefundStatus.Pending,
            Origin = PaymentRefundOrigin.Host,
        };
        pending.IdempotencyKey = $"payment-refund:{pending.Id:N}";
        await using (var db = NewContext())
        {
            db.PaymentRefunds.Add(pending);
            await db.SaveChangesAsync();
        }

        await HandleAsync(
            RefundEvent("refund.updated", seed.PaymentIntentId, "re_lost", 12_000, "succeeded", Account, pending.Id),
            WebhookSource.Connected);

        await using var check = NewContext();
        var refund = await check.PaymentRefunds.SingleAsync();
        Assert.Equal(pending.Id, refund.Id);
        Assert.Equal("re_lost", refund.StripeRefundId);
        Assert.Equal(PaymentRefundStatus.Succeeded, refund.Status);
        Assert.Equal(PaymentStatus.PartiallyRefunded, (await check.Payments.SingleAsync(p => p.Id == seed.PaymentId)).Status);
    }

    [PostgresFact]
    public async Task CancelAsync_DoubleClickInParallel_CancelsAndRefundsOnce()
    {
        var seed = await SeedPaidBookingAsync(300m, freeRefundDeadline: DateTime.UtcNow.Date.AddDays(10));
        var refundRequests = 0;
        _stripe
            .Setup(s => s.CreateRefundAsync(It.IsAny<StripeRefundCreateRequest>(), It.IsAny<CancellationToken>()))
            .Returns(async (StripeRefundCreateRequest r, CancellationToken _) =>
            {
                Interlocked.Increment(ref refundRequests);
                await Task.Delay(50);
                return new Refund { Id = $"re_{Guid.NewGuid():N}", PaymentIntentId = r.PaymentIntentId, Amount = r.AmountCents, Status = "succeeded" };
            });

        await using var first = NewContext();
        await using var second = NewContext();
        var attempts = await Task.WhenAll(
            Attempt(() => CancellationService(first).CancelAsync(new BookingCancellationRequest(seed.BookingId, 300m, null, "auth0|host"))),
            Attempt(() => CancellationService(second).CancelAsync(new BookingCancellationRequest(seed.BookingId, 300m, null, "auth0|host"))));

        Assert.Single(attempts, a => a is null);
        Assert.Single(attempts, a => a is DomainConflictException { Code: "booking_already_cancelled" });
        Assert.Equal(1, refundRequests);
        await using var db = NewContext();
        Assert.Equal(BookingStatus.Cancelled, (await db.Bookings.SingleAsync(b => b.Id == seed.BookingId)).Status);
        var refund = await db.PaymentRefunds.SingleAsync();
        Assert.Equal(300m, refund.Amount);
        Assert.Equal(PaymentRefundOrigin.BookingCancellation, refund.Origin);
        Assert.Equal(PaymentStatus.Refunded, (await db.Payments.SingleAsync(p => p.Id == seed.PaymentId)).Status);
    }

    private static async Task<Exception?> Attempt(Func<Task> action)
    {
        try
        {
            await action();
            return null;
        }
        catch (Exception ex)
        {
            return ex;
        }
    }

    private async Task HandleAsync(Event stripeEvent, WebhookSource source)
    {
        await using var db = NewContext();
        var handler = new StripeWebhookHandler(
            new PaymentRepository(db),
            new BookingRepository(db),
            Mock.Of<IConnectOnboardingService>(),
            db,
            new FakeStripeBillingService(Config),
            new EntitlementService(db, Config),
            new VatCalculationService(),
            Mock.Of<IOssRevenueTracker>(),
            Mock.Of<ISdiEInvoiceService>(),
            Mock.Of<IRentBillingService>(),
            RefundService(db),
            TestCheckoutPaymentSettlement.Create(db, stripe: _stripe.Object, emails: _emails.Object),
            NullLogger<StripeWebhookHandler>.Instance);
        await handler.HandleEventAsync(stripeEvent, source);
    }

    private PaymentRefundService RefundService(AppDbContext db) => new(
        db,
        _stripe.Object,
        Mock.Of<IPaymentRefundRetryScheduler>(),
        _emails.Object,
        NullLogger<PaymentRefundService>.Instance);

    private BookingCancellationService CancellationService(AppDbContext db) =>
        new(db, RefundService(db), _stripe.Object, NullLogger<BookingCancellationService>.Instance);

    private AppDbContext NewContext() => _database!.CreateContext();

    private static Event ChargeRefunded(string paymentIntentId, string account) => new()
    {
        Id = StripeTestEvents.NewEventId(),
        Type = "charge.refunded",
        Account = account,
        Data = new EventData
        {
            Object = new Charge { Id = $"ch_{Guid.NewGuid():N}", PaymentIntentId = paymentIntentId, Refunded = true },
        },
    };

    private static Event RefundEvent(
        string type,
        string paymentIntentId,
        string refundId,
        long amountCents,
        string status,
        string account,
        Guid? paymentRefundId = null) => new()
        {
            Id = StripeTestEvents.NewEventId(),
            Type = type,
            Account = account,
            Data = new EventData
            {
                Object = new Refund
                {
                    Id = refundId,
                    PaymentIntentId = paymentIntentId,
                    Amount = amountCents,
                    Status = status,
                    Metadata = paymentRefundId is { } id
                        ? new Dictionary<string, string> { ["paymentRefundId"] = id.ToString() }
                        : new Dictionary<string, string>(),
                },
            },
        };

    private sealed record PaidBookingSeed(Guid OrgId, Guid BookingId, Guid PaymentId, string PaymentIntentId, string GuestEmail);

    private async Task<PaidBookingSeed> SeedPaidBookingAsync(decimal amount, DateTime? freeRefundDeadline = null)
    {
        var org = new OrgEntity
        {
            Name = "Refund Org",
            Slug = $"refund-{Guid.NewGuid():N}",
            DisplayName = "Refund Org",
            ContactEmail = "host@example.com",
            StripeConnectedAccountId = Account,
            ConnectChargesEnabled = true,
            IsActive = true,
        };
        var property = new Property
        {
            OrgId = org.Id,
            OwnerId = "auth0|host",
            Name = "Villa Refund",
            Address = $"Via Rimborso {Guid.NewGuid():N}",
            City = "Rome",
            PostalCode = "00100",
            MaxGuests = 4,
            NightlyRate = 100m,
            IsActive = true,
        };
        var guest = new Guest
        {
            OrgId = org.Id,
            FirstName = "Anna",
            LastName = "Verdi",
            Email = $"guest.{Guid.NewGuid():N}@example.com",
        };
        var booking = new Booking
        {
            OrgId = org.Id,
            PropertyId = property.Id,
            GuestId = guest.Id,
            CheckInDate = DateTime.UtcNow.Date.AddDays(30),
            CheckOutDate = DateTime.UtcNow.Date.AddDays(33),
            NumberOfGuests = 2,
            Status = BookingStatus.Confirmed,
            Source = BookingSource.Direct,
            TotalPrice = amount,
            FreeRefundDeadline = freeRefundDeadline,
        };
        var paymentIntentId = $"pi_{Guid.NewGuid():N}";
        var payment = new Payment
        {
            OrgId = org.Id,
            BookingId = booking.Id,
            Amount = amount,
            Status = PaymentStatus.Completed,
            Method = PaymentMethod.CreditCard,
            TransactionId = paymentIntentId,
            StripePaymentIntentId = paymentIntentId,
            StripeAccountId = Account,
        };

        await using var db = NewContext();
        db.AddRange(org, property, guest, booking, payment);
        await db.SaveChangesAsync();
        return new PaidBookingSeed(org.Id, booking.Id, payment.Id, paymentIntentId, guest.Email);
    }
}
