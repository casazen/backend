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
using Xunit;

namespace Casazen.Tests.Unit.Infrastructure;

/// <summary>
/// Proofs of the ConcordatoFacile payment webhook (ADR-005): the money rule (only EUR 119,00 grants access), the data rule
/// (a duplicate event never credits twice) and the refund rule (the purchase is marked, nothing is deleted).
/// </summary>
public class ConcordatoFacilePaymentHandlerTests
{
    private static readonly DateTime EventTime = new(2026, 10, 24, 8, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task PaymentIntentSucceeded_Amount11900EUR_GrantsAccess()
    {
        await using var db = NewDb();
        var purchase = await SeedPurchaseAsync(db);
        var handler = new ConcordatoFacilePaymentHandler(db, NullLogger<ConcordatoFacilePaymentHandler>.Instance);

        await handler.HandlePaymentIntentSucceededAsync(PaymentIntentFor(purchase, "pi_cf_ok", 11900, "eur"), EventTime);

        var saved = await db.ConcordatoFacilePurchases.AsNoTracking().SingleAsync();
        Assert.Equal(ConcordatoFacilePurchaseStatus.Completed, saved.Status);
        Assert.Equal(EventTime, saved.PaidAt);
        Assert.Equal("pi_cf_ok", saved.StripePaymentIntentId);
    }

    [Theory]
    [InlineData(5000L, "eur")]
    [InlineData(11900L, "usd")]
    public async Task PaymentIntentSucceeded_WrongAmount_DoesNotGrantAccess(long amount, string currency)
    {
        await using var db = NewDb();
        var purchase = await SeedPurchaseAsync(db);
        var handler = new ConcordatoFacilePaymentHandler(db, NullLogger<ConcordatoFacilePaymentHandler>.Instance);

        await handler.HandlePaymentIntentSucceededAsync(PaymentIntentFor(purchase, "pi_cf_wrong", amount, currency), EventTime);

        var saved = await db.ConcordatoFacilePurchases.AsNoTracking().SingleAsync();
        Assert.Equal(ConcordatoFacilePurchaseStatus.Pending, saved.Status);
        Assert.Null(saved.PaidAt);
        Assert.Null(saved.StripePaymentIntentId);
    }

    [Fact]
    public async Task DuplicateEvent_IdempotentSuccess_NoDoubleCredit()
    {
        await using var db = NewDb();
        var purchase = await SeedPurchaseAsync(db);
        var webhook = CreateWebhookHandler(db);
        var stripeEvent = EventOf("evt_cf_duplicate", "payment_intent.succeeded", PaymentIntentFor(purchase, "pi_cf_dup", 11900, "eur"));

        await webhook.HandleEventAsync(stripeEvent, WebhookSource.Platform);

        var claim = await db.ProcessedStripeEvents.AsNoTracking().SingleAsync(e => e.EventId == "evt_cf_duplicate");
        Assert.Equal(WebhookSource.ConcordatoFacile, claim.Source);
        Assert.Equal(ConcordatoFacilePurchaseStatus.Completed, (await db.ConcordatoFacilePurchases.AsNoTracking().SingleAsync()).Status);

        // Stripe delivers the same event again: if it were applied it would complete the purchase once more.
        var tracked = await db.ConcordatoFacilePurchases.SingleAsync();
        tracked.Status = ConcordatoFacilePurchaseStatus.Pending;
        tracked.PaidAt = null;
        await db.SaveChangesAsync();

        await webhook.HandleEventAsync(stripeEvent, WebhookSource.Platform);

        var saved = await db.ConcordatoFacilePurchases.AsNoTracking().SingleAsync();
        Assert.Equal(ConcordatoFacilePurchaseStatus.Pending, saved.Status);
        Assert.Null(saved.PaidAt);
        Assert.Equal(1, await db.ProcessedStripeEvents.CountAsync(e => e.EventId == "evt_cf_duplicate"));
    }

    [Fact]
    public async Task ChargeRefunded_FullRefund_MarksTheCompletedPurchaseRefunded()
    {
        await using var db = NewDb();
        var purchase = await SeedPurchaseAsync(db, ConcordatoFacilePurchaseStatus.Completed, "pi_cf_refund");
        var webhook = CreateWebhookHandler(db);

        await webhook.HandleEventAsync(
            EventOf("evt_cf_refund", "charge.refunded", ChargeFor("pi_cf_refund", refunded: true)),
            WebhookSource.Platform);

        var saved = await db.ConcordatoFacilePurchases.AsNoTracking().SingleAsync(p => p.Id == purchase.Id);
        Assert.Equal(ConcordatoFacilePurchaseStatus.Refunded, saved.Status);
        Assert.NotNull(saved.RefundedAt);
        Assert.Equal(WebhookSource.ConcordatoFacile, (await db.ProcessedStripeEvents.AsNoTracking().SingleAsync()).Source);
    }

    [Fact]
    public async Task ChargeRefunded_PartialRefund_KeepsTheAccess()
    {
        await using var db = NewDb();
        var purchase = await SeedPurchaseAsync(db, ConcordatoFacilePurchaseStatus.Completed, "pi_cf_partial");
        var webhook = CreateWebhookHandler(db);

        await webhook.HandleEventAsync(
            EventOf("evt_cf_partial", "charge.refunded", ChargeFor("pi_cf_partial", refunded: false)),
            WebhookSource.Platform);

        var saved = await db.ConcordatoFacilePurchases.AsNoTracking().SingleAsync(p => p.Id == purchase.Id);
        Assert.Equal(ConcordatoFacilePurchaseStatus.Completed, saved.Status);
        Assert.Null(saved.RefundedAt);
    }

    private static AppDbContext NewDb()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"concordato-facile-payment-{Guid.NewGuid()}")
            .Options;
        return new AppDbContext(options);
    }

    private static async Task<ConcordatoFacilePurchase> SeedPurchaseAsync(
        AppDbContext db,
        ConcordatoFacilePurchaseStatus status = ConcordatoFacilePurchaseStatus.Pending,
        string? paymentIntentId = null)
    {
        var purchase = new ConcordatoFacilePurchase
        {
            OrgId = Guid.NewGuid(),
            LeaseContractId = Guid.NewGuid(),
            StripeCheckoutSessionId = $"cs_test_{Guid.NewGuid():N}",
            StripePaymentIntentId = paymentIntentId,
            AmountCents = 11900,
            Currency = "eur",
            Status = status,
        };
        db.ConcordatoFacilePurchases.Add(purchase);
        await db.SaveChangesAsync();
        return purchase;
    }

    private static PaymentIntent PaymentIntentFor(ConcordatoFacilePurchase purchase, string id, long amount, string currency) =>
        new()
        {
            Id = id,
            Amount = amount,
            Currency = currency,
            Created = EventTime.AddMinutes(-5),
            Metadata = new Dictionary<string, string>
            {
                ["orgId"] = purchase.OrgId.ToString(),
                ["leaseId"] = purchase.LeaseContractId.ToString(),
            },
        };

    private static Charge ChargeFor(string paymentIntentId, bool refunded) =>
        new()
        {
            Id = $"ch_{Guid.NewGuid():N}",
            PaymentIntentId = paymentIntentId,
            Amount = 11900,
            AmountRefunded = refunded ? 11900 : 2000,
            Refunded = refunded,
        };

    private static Event EventOf(string id, string type, IHasObject payload) =>
        new()
        {
            Id = id,
            Type = type,
            Created = EventTime,
            Data = new EventData { Object = payload },
        };

    private static StripeWebhookHandler CreateWebhookHandler(AppDbContext db) =>
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
            NullLogger<StripeWebhookHandler>.Instance);
}
