using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.External;
using Casazen.Infrastructure.Repositories;
using Casazen.Infrastructure.Services;
using Casazen.Tests.Integration;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Stripe;
using Stripe.Checkout;
using Xunit;

namespace Casazen.Tests.Unit.Infrastructure;

/// <summary>
/// CF-BE, the PROOF of the money rule of the failed payments (closes CF-FIX-FU2): <c>payment_intent.payment_failed</c> and
/// <c>payment_intent.canceled</c> of the service close its purchase ONLY when the Checkout Session can no longer succeed. A card declined
/// while the Session is open leaves the purchase pending (the customer tries another card); an expired Session, or one completed with a
/// delayed payment that failed, closes it (so a new checkout is possible); a purchase that is paid, or is not pending, is never touched.
/// Stripe is played by a mocked <see cref="IStripeClient"/> that answers the list of Sessions of the PaymentIntent.
/// </summary>
public class ConcordatoFacilePaymentEndedTests
{
    private static readonly DateTime EventTime = new(2026, 10, 25, 9, 0, 0, DateTimeKind.Utc);

    private readonly Mock<IStripeClient> _client = new();
    private Session? _sessionOfThePaymentIntent;

    public ConcordatoFacilePaymentEndedTests()
    {
        _client.SetupGet(c => c.ApiBase).Returns("https://api.stripe.com");
        _client
            .Setup(c => c.RequestAsync<StripeList<Session>>(
                It.IsAny<HttpMethod>(), It.IsAny<string>(), It.IsAny<BaseOptions>(), It.IsAny<RequestOptions>(), It.IsAny<CancellationToken>()))
            .Returns(() =>
            {
                var sessions = new List<Session>();
                if (_sessionOfThePaymentIntent is not null)
                    sessions.Add(_sessionOfThePaymentIntent);

                return Task.FromResult(new StripeList<Session> { Data = sessions });
            });
    }

    [Theory]
    [InlineData("open", "unpaid", ConcordatoFacilePurchaseStatus.Pending)] // a card declined: the customer can try another one
    [InlineData("expired", "unpaid", ConcordatoFacilePurchaseStatus.Failed)] // nobody can pay it any more
    [InlineData("complete", "unpaid", ConcordatoFacilePurchaseStatus.Failed)] // a delayed payment that failed: a completed Session is not tried again
    [InlineData("complete", "paid", ConcordatoFacilePurchaseStatus.Pending)] // paid: never closed (the success event completes it)
    public async Task PaymentFailed_ClosesThePurchaseOnlyWhenTheSessionCanNoLongerSucceed(
        string sessionStatus, string paymentStatus, ConcordatoFacilePurchaseStatus expected)
    {
        await using var db = NewDb();
        var purchase = await SeedPurchaseAsync(db, ConcordatoFacilePurchaseStatus.Pending);
        _sessionOfThePaymentIntent = new Session { Id = purchase.StripeCheckoutSessionId, Status = sessionStatus, PaymentStatus = paymentStatus };

        await Handler(db).HandlePaymentNotCompletedAsync(PaymentIntentFor(purchase), "payment_intent.payment_failed");

        Assert.Equal(expected, (await db.ConcordatoFacilePurchases.AsNoTracking().SingleAsync()).Status);
    }

    [Theory]
    [InlineData(ConcordatoFacilePurchaseStatus.Completed)]
    [InlineData(ConcordatoFacilePurchaseStatus.Refunded)]
    [InlineData(ConcordatoFacilePurchaseStatus.Failed)]
    public async Task PaymentCanceled_ANotPendingPurchase_IsNeverTouched(ConcordatoFacilePurchaseStatus status)
    {
        await using var db = NewDb();
        var purchase = await SeedPurchaseAsync(db, status);
        _sessionOfThePaymentIntent = new Session { Id = purchase.StripeCheckoutSessionId, Status = "expired", PaymentStatus = "unpaid" };

        await Handler(db).HandlePaymentNotCompletedAsync(PaymentIntentFor(purchase), "payment_intent.canceled");

        var saved = await db.ConcordatoFacilePurchases.AsNoTracking().SingleAsync();
        Assert.Equal(status, saved.Status);
        Assert.Equal(purchase.PaidAt, saved.PaidAt);
    }

    /// <summary>
    /// The whole road of the event: Stripe cancels the PaymentIntent of an expired Session, the webhook closes the purchase and claims the
    /// event under the ConcordatoFacile source; the same delivery again changes nothing.
    /// </summary>
    [Fact]
    public async Task WebhookPaymentCanceled_OfAnExpiredSession_ClosesThePurchase_OnceAndUnderTheServiceSource()
    {
        await using var db = NewDb();
        var purchase = await SeedPurchaseAsync(db, ConcordatoFacilePurchaseStatus.Pending);
        _sessionOfThePaymentIntent = new Session { Id = purchase.StripeCheckoutSessionId, Status = "expired", PaymentStatus = "unpaid" };
        var webhook = CreateWebhookHandler(db);
        var stripeEvent = new Event
        {
            Id = "evt_cf_canceled",
            Type = "payment_intent.canceled",
            Created = EventTime,
            Data = new EventData { Object = PaymentIntentFor(purchase) },
        };

        await webhook.HandleEventAsync(stripeEvent, WebhookSource.Platform);

        Assert.Equal(ConcordatoFacilePurchaseStatus.Failed, (await db.ConcordatoFacilePurchases.AsNoTracking().SingleAsync()).Status);
        Assert.Equal(WebhookSource.ConcordatoFacile, (await db.ProcessedStripeEvents.AsNoTracking().SingleAsync(e => e.EventId == "evt_cf_canceled")).Source);

        // Delivered again, with the purchase back to pending: a duplicate is skipped, not applied.
        var tracked = await db.ConcordatoFacilePurchases.SingleAsync();
        tracked.Status = ConcordatoFacilePurchaseStatus.Pending;
        await db.SaveChangesAsync();

        await webhook.HandleEventAsync(stripeEvent, WebhookSource.Platform);

        Assert.Equal(ConcordatoFacilePurchaseStatus.Pending, (await db.ConcordatoFacilePurchases.AsNoTracking().SingleAsync()).Status);
    }

    private ConcordatoFacilePaymentHandler Handler(AppDbContext db) =>
        new(db, NullLogger<ConcordatoFacilePaymentHandler>.Instance, _client.Object);

    private StripeWebhookHandler CreateWebhookHandler(AppDbContext db) =>
        new(
            new PaymentRepository(db),
            new BookingRepository(db),
            Mock.Of<IConnectOnboardingService>(),
            db,
            Mock.Of<IStripeBillingService>(),
            Mock.Of<IEntitlementService>(),
            Mock.Of<IPlatformInvoiceService>(),
            Mock.Of<IRentBillingService>(),
            Mock.Of<IPaymentRefundService>(),
            TestCheckoutPaymentSettlement.Create(db),
            TestDeferredCharges.Create(db),
            Mock.Of<ISupplierPaymentWebhookService>(),
            NullLogger<StripeWebhookHandler>.Instance,
            concordatoFacilePayments: Handler(db));

    private static AppDbContext NewDb() =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase($"concordato-facile-ended-{Guid.NewGuid()}").Options);

    private static async Task<ConcordatoFacilePurchase> SeedPurchaseAsync(AppDbContext db, ConcordatoFacilePurchaseStatus status)
    {
        var purchase = new ConcordatoFacilePurchase
        {
            OrgId = Guid.NewGuid(),
            LeaseContractId = Guid.NewGuid(),
            StripeCheckoutSessionId = $"cs_test_{Guid.NewGuid():N}",
            AmountCents = 11900,
            Currency = "eur",
            Status = status,
            CreatedAt = EventTime.AddMinutes(-30),
            PaidAt = status is ConcordatoFacilePurchaseStatus.Completed or ConcordatoFacilePurchaseStatus.Refunded ? EventTime.AddMinutes(-20) : null,
        };
        db.ConcordatoFacilePurchases.Add(purchase);
        await db.SaveChangesAsync();
        return purchase;
    }

    private static PaymentIntent PaymentIntentFor(ConcordatoFacilePurchase purchase) =>
        new()
        {
            Id = $"pi_cf_{Guid.NewGuid():N}",
            Amount = 11900,
            Currency = "eur",
            Created = EventTime.AddMinutes(-30),
            Metadata = new Dictionary<string, string>
            {
                ["orgId"] = purchase.OrgId.ToString(),
                ["leaseId"] = purchase.LeaseContractId.ToString(),
            },
        };
}
