using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.External;
using Casazen.Infrastructure.Repositories;
using Casazen.Infrastructure.Services;
using Casazen.Tests.Integration;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Stripe;
using PlanTier = Casazen.Core.Entities.Enums.PlanTier;
using Xunit;

namespace Casazen.Tests.Unit.Infrastructure;

/// <summary>
/// Platform billing webhooks (A1-11): the tier is granted only by an active or trialing subscription, and events
/// processed out of order or belonging to another subscription of the customer never reopen paid access.
/// </summary>
public class StripeWebhookSubscriptionStateTests
{
    private const string ProPrice = "price_test_pro";
    private const string ScalePrice = "price_test_scale";

    private static readonly IConfiguration Config = new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Billing:Prices:Pro"] = ProPrice,
            ["Billing:Prices:Scale"] = ScalePrice,
            ["Billing:PastDueGraceDays"] = "7",
        })
        .Build();

    private readonly AppDbContext _db = new(new DbContextOptionsBuilder<AppDbContext>()
        .UseInMemoryDatabase($"stripe-subscription-state-{Guid.NewGuid()}")
        .Options);

    [Fact]
    public async Task HandleEventAsync_IncompleteSubscriptionCreated_KeepsStarter()
    {
        var org = await SeedOrgAsync(SubscriptionStatus.None, PlanTier.Starter, subscriptionId: null);

        await HandleAsync(Subscription("customer.subscription.created", "sub_new", "incomplete", org, ScalePrice));

        var stored = await ReloadAsync(org.Id);
        Assert.Equal(SubscriptionStatus.Incomplete, stored.SubscriptionStatus);
        Assert.Equal("sub_new", stored.SubscriptionId);
        Assert.Equal(PlanTier.Starter, stored.PlanTier);
    }

    [Fact]
    public async Task HandleEventAsync_SubscriptionBecomesActive_GrantsPriceTier()
    {
        var org = await SeedOrgAsync(SubscriptionStatus.Incomplete, PlanTier.Starter, subscriptionId: "sub_new");

        await HandleAsync(Subscription("customer.subscription.updated", "sub_new", "active", org, ScalePrice));

        var stored = await ReloadAsync(org.Id);
        Assert.Equal(SubscriptionStatus.Active, stored.SubscriptionStatus);
        Assert.Equal(PlanTier.Scale, stored.PlanTier);
    }

    [Fact]
    public async Task HandleEventAsync_CanceledSubscriptionUpdatedLate_StaysCanceled()
    {
        var org = await SeedOrgAsync(SubscriptionStatus.Canceled, PlanTier.Starter, subscriptionId: "sub_old");

        await HandleAsync(Subscription("customer.subscription.updated", "sub_old", "active", org, ScalePrice));

        var stored = await ReloadAsync(org.Id);
        Assert.Equal(SubscriptionStatus.Canceled, stored.SubscriptionStatus);
        Assert.Equal(PlanTier.Starter, stored.PlanTier);
    }

    [Fact]
    public async Task HandleEventAsync_OtherSubscriptionOfCustomerDeleted_KeepsPayingSubscription()
    {
        var org = await SeedOrgAsync(SubscriptionStatus.Active, PlanTier.Pro, subscriptionId: "sub_paying");

        await HandleAsync(Subscription("customer.subscription.deleted", "sub_duplicate", "canceled", org, ProPrice));

        var stored = await ReloadAsync(org.Id);
        Assert.Equal("sub_paying", stored.SubscriptionId);
        Assert.Equal(SubscriptionStatus.Active, stored.SubscriptionStatus);
        Assert.Equal(PlanTier.Pro, stored.PlanTier);
    }

    [Fact]
    public async Task HandleEventAsync_SecondLiveSubscription_KeepsCurrentOne()
    {
        var org = await SeedOrgAsync(SubscriptionStatus.Active, PlanTier.Pro, subscriptionId: "sub_paying");

        await HandleAsync(Subscription("customer.subscription.created", "sub_duplicate", "active", org, ScalePrice));

        var stored = await ReloadAsync(org.Id);
        Assert.Equal("sub_paying", stored.SubscriptionId);
        Assert.Equal(PlanTier.Pro, stored.PlanTier);
    }

    [Fact]
    public async Task HandleEventAsync_NewSubscriptionAfterCanceledOne_Applies()
    {
        var org = await SeedOrgAsync(SubscriptionStatus.Canceled, PlanTier.Starter, subscriptionId: "sub_old");

        await HandleAsync(Subscription("customer.subscription.created", "sub_new", "active", org, ProPrice));

        var stored = await ReloadAsync(org.Id);
        Assert.Equal("sub_new", stored.SubscriptionId);
        Assert.Equal(SubscriptionStatus.Active, stored.SubscriptionStatus);
        Assert.Equal(PlanTier.Pro, stored.PlanTier);
    }

    [Fact]
    public async Task HandleEventAsync_InvoicePaidForCanceledSubscription_DoesNotReactivate()
    {
        var org = await SeedOrgAsync(SubscriptionStatus.Canceled, PlanTier.Starter, subscriptionId: "sub_old");

        await HandleAsync(Invoice("invoice.paid", "sub_old", org, "subscription_cycle"));

        Assert.Equal(SubscriptionStatus.Canceled, (await ReloadAsync(org.Id)).SubscriptionStatus);
    }

    [Fact]
    public async Task HandleEventAsync_InvoicePaidForAnotherSubscription_KeepsCurrentStatus()
    {
        var org = await SeedOrgAsync(SubscriptionStatus.Unpaid, PlanTier.Pro, subscriptionId: "sub_current");

        await HandleAsync(Invoice("invoice.paid", "sub_other", org, "subscription_cycle"));

        var stored = await ReloadAsync(org.Id);
        Assert.Equal("sub_current", stored.SubscriptionId);
        Assert.Equal(SubscriptionStatus.Unpaid, stored.SubscriptionStatus);
    }

    [Fact]
    public async Task HandleEventAsync_InvoicePaidAfterPastDue_ReactivatesSubscription()
    {
        var org = await SeedOrgAsync(SubscriptionStatus.PastDue, PlanTier.Pro, subscriptionId: "sub_current", pastDueSince: DateTime.UtcNow.AddDays(-2));

        await HandleAsync(Invoice("invoice.paid", "sub_current", org, "subscription_cycle"));

        var stored = await ReloadAsync(org.Id);
        Assert.Equal(SubscriptionStatus.Active, stored.SubscriptionStatus);
        Assert.Null(stored.PastDueSince);
        Assert.Equal(1, await _db.PlatformInvoices.CountAsync(i => i.OrgId == org.Id));
    }

    [Fact]
    public async Task HandleEventAsync_RenewalPaymentFailed_StartsPastDueGrace()
    {
        var org = await SeedOrgAsync(SubscriptionStatus.Active, PlanTier.Pro, subscriptionId: "sub_current");

        await HandleAsync(Invoice("invoice.payment_failed", "sub_current", org, "subscription_cycle"));

        var stored = await ReloadAsync(org.Id);
        Assert.Equal(SubscriptionStatus.PastDue, stored.SubscriptionStatus);
        Assert.NotNull(stored.PastDueSince);
    }

    [Fact]
    public async Task HandleEventAsync_FirstPaymentFailed_StaysIncompleteWithoutGrace()
    {
        var org = await SeedOrgAsync(SubscriptionStatus.Incomplete, PlanTier.Starter, subscriptionId: "sub_new");

        await HandleAsync(Invoice("invoice.payment_failed", "sub_new", org, "subscription_create"));

        var stored = await ReloadAsync(org.Id);
        Assert.Equal(SubscriptionStatus.Incomplete, stored.SubscriptionStatus);
        Assert.Null(stored.PastDueSince);
    }

    [Fact]
    public async Task HandleEventAsync_PastDueOfNewSubscription_DoesNotInheritOldGraceStart()
    {
        var oldStart = DateTime.UtcNow.AddDays(-30);
        var org = await SeedOrgAsync(SubscriptionStatus.Canceled, PlanTier.Starter, subscriptionId: "sub_old", pastDueSince: oldStart);

        await HandleAsync(Subscription("customer.subscription.updated", "sub_new", "past_due", org, ProPrice));

        var stored = await ReloadAsync(org.Id);
        Assert.Equal(SubscriptionStatus.PastDue, stored.SubscriptionStatus);
        Assert.NotNull(stored.PastDueSince);
        Assert.True(stored.PastDueSince > oldStart);
    }

    private async Task<OrgEntity> SeedOrgAsync(
        SubscriptionStatus status,
        PlanTier tier,
        string? subscriptionId,
        DateTime? pastDueSince = null)
    {
        var org = new OrgEntity
        {
            Name = "Billing Org",
            Slug = $"billing-{Guid.NewGuid():N}",
            DisplayName = "Billing Org",
            ContactEmail = "billing@example.com",
            PlanTier = tier,
            StripeCustomerId = $"cus_test_{Guid.NewGuid():N}",
            SubscriptionId = subscriptionId,
            SubscriptionStatus = status,
            PastDueSince = pastDueSince,
            IsActive = true,
        };
        _db.Orgs.Add(org);
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();
        return org;
    }

    private async Task<OrgEntity> ReloadAsync(Guid orgId)
    {
        _db.ChangeTracker.Clear();
        return await _db.Orgs.AsNoTracking().SingleAsync(o => o.Id == orgId);
    }

    private async Task HandleAsync(Event stripeEvent)
    {
        var handler = new StripeWebhookHandler(
            new PaymentRepository(_db),
            new BookingRepository(_db),
            Mock.Of<IConnectOnboardingService>(),
            _db,
            new FakeStripeBillingService(Config),
            new EntitlementService(_db, Config),
            new VatCalculationService(),
            Mock.Of<IOssRevenueTracker>(),
            Mock.Of<ISdiEInvoiceService>(),
            Mock.Of<IRentBillingService>(),
            NullLogger<StripeWebhookHandler>.Instance);
        await handler.HandleEventAsync(stripeEvent, WebhookSource.Platform);
        _db.ChangeTracker.Clear();
    }

    private static Event Subscription(string type, string subscriptionId, string status, OrgEntity org, string priceId) =>
        StripeTestEvents.Subscription(type, subscriptionId, status, org.Id, org.StripeCustomerId!, priceId);

    private static Event Invoice(string type, string subscriptionId, OrgEntity org, string billingReason) =>
        StripeTestEvents.Invoice(type, subscriptionId, org.StripeCustomerId!, billingReason);
}
