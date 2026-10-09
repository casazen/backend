using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Features;
using Casazen.Core.OrgTeam;
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
using Xunit;
using PlanTier = Casazen.Core.Entities.Enums.PlanTier;

namespace Casazen.Tests.Unit.Infrastructure;

/// <summary>
/// AM-02b: the plan changes that nobody asks for — a subscription that becomes active gives the tier of its price, a canceled
/// one takes the plan back to Starter — are told to the activity log by the webhook, in the very save that writes the tier,
/// with no actor and the source <c>subscription</c>. A repeated or ignored event says nothing; with the <c>OrgTeam</c> flag off
/// nothing is collected and the plan changes exactly as before.
/// </summary>
public class StripeWebhookPlanActivityTests
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
        .UseInMemoryDatabase($"stripe-plan-activity-{Guid.NewGuid()}")
        .Options);

    private bool _orgTeamFlag = true;

    [Fact]
    public async Task SubscriptionBecomesActive_WritesThePlanChange_WithNoActor()
    {
        var org = await SeedOrgAsync(SubscriptionStatus.Incomplete, PlanTier.Starter, "sub_new");

        await HandleAsync(Subscription("customer.subscription.updated", "sub_new", "active", org, ScalePrice));

        var line = Assert.Single(await LinesAsync(org.Id));
        Assert.Equal(
            (OrgActivityType.PlanChanged, null, org.Id.ToString(), OrgActivitySubjectType.Org),
            (line.Type, line.ActorUserId, line.SubjectId, line.SubjectType));
        Assert.Equal(
            new Dictionary<string, string> { ["fromTier"] = "Starter", ["toTier"] = "Scale", ["source"] = "subscription" },
            OrgActivityDetails.Parse(line.DetailsJson));
        Assert.Equal(PlanTier.Scale, (await ReloadAsync(org.Id)).PlanTier);
    }

    [Fact]
    public async Task TheSameEventAgain_SaysNothingMore()
    {
        var org = await SeedOrgAsync(SubscriptionStatus.Incomplete, PlanTier.Starter, "sub_new");

        await HandleAsync(Subscription("customer.subscription.updated", "sub_new", "active", org, ProPrice));
        await HandleAsync(Subscription("customer.subscription.updated", "sub_new", "active", org, ProPrice));

        Assert.Single(await LinesAsync(org.Id));
    }

    [Fact]
    public async Task AnUpgradeFromProToScale_ThenACancellation_AreTwoLines_InOrder()
    {
        var org = await SeedOrgAsync(SubscriptionStatus.Active, PlanTier.Pro, "sub_paying");

        await HandleAsync(Subscription("customer.subscription.updated", "sub_paying", "active", org, ScalePrice));
        await HandleAsync(Subscription("customer.subscription.deleted", "sub_paying", "canceled", org, ScalePrice));

        var lines = (await LinesAsync(org.Id)).Select(l => OrgActivityDetails.Parse(l.DetailsJson)).ToList();
        Assert.Equal(2, lines.Count);
        Assert.Contains(lines, d => d["fromTier"] == "Pro" && d["toTier"] == "Scale");
        Assert.Contains(lines, d => d["fromTier"] == "Scale" && d["toTier"] == "Starter");
        Assert.All(lines, d => Assert.Equal("subscription", d["source"]));
        Assert.Equal(PlanTier.Starter, (await ReloadAsync(org.Id)).PlanTier);
    }

    [Fact]
    public async Task ASubscriptionThatGrantsNothing_SaysNothing()
    {
        var org = await SeedOrgAsync(SubscriptionStatus.None, PlanTier.Starter, null);

        await HandleAsync(Subscription("customer.subscription.created", "sub_new", "incomplete", org, ScalePrice));

        Assert.Empty(await LinesAsync(org.Id));
        Assert.Equal(PlanTier.Starter, (await ReloadAsync(org.Id)).PlanTier);
    }

    [Fact]
    public async Task AnEventThatIsIgnored_SaysNothing()
    {
        var org = await SeedOrgAsync(SubscriptionStatus.Active, PlanTier.Pro, "sub_paying");

        await HandleAsync(Subscription("customer.subscription.created", "sub_duplicate", "active", org, ScalePrice));

        Assert.Empty(await LinesAsync(org.Id));
        Assert.Equal(PlanTier.Pro, (await ReloadAsync(org.Id)).PlanTier);
    }

    [Fact]
    public async Task WithTheFlagOff_ThePlanChangesAsBefore_AndNothingIsCollected()
    {
        _orgTeamFlag = false;
        var org = await SeedOrgAsync(SubscriptionStatus.Incomplete, PlanTier.Starter, "sub_new");

        await HandleAsync(Subscription("customer.subscription.updated", "sub_new", "active", org, ProPrice));

        Assert.Empty(await LinesAsync(org.Id));
        Assert.Equal(PlanTier.Pro, (await ReloadAsync(org.Id)).PlanTier);
    }

    private ActivityLog Activity()
    {
        var flags = new Mock<IFeatureFlags>();
        flags.Setup(f => f.IsEnabled(FeatureFlags.OrgTeam)).Returns(_orgTeamFlag);
        return new ActivityLog(_db, flags.Object);
    }

    private async Task<List<OrgActivityEntry>> LinesAsync(Guid orgId)
    {
        _db.ChangeTracker.Clear();
        return await _db.OrgActivityEntries.IgnoreQueryFilters().AsNoTracking().Where(e => e.OrgId == orgId).OrderBy(e => e.When).ToListAsync();
    }

    private async Task<OrgEntity> SeedOrgAsync(SubscriptionStatus status, PlanTier tier, string? subscriptionId)
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
        var activity = Activity();
        var handler = new StripeWebhookHandler(
            new PaymentRepository(_db),
            new BookingRepository(_db),
            Mock.Of<IConnectOnboardingService>(),
            _db,
            new FakeStripeBillingService(Config),
            new EntitlementService(_db, Config, activity),
            TestPlatformInvoices.Create(_db, new FakeStripeBillingService(Config)),
            Mock.Of<IRentBillingService>(),
            Mock.Of<IPaymentRefundService>(),
            TestCheckoutPaymentSettlement.Create(_db),
            TestDeferredCharges.Create(_db),
            Mock.Of<ISupplierPaymentWebhookService>(),
            NullLogger<StripeWebhookHandler>.Instance,
            activity);
        await handler.HandleEventAsync(stripeEvent, WebhookSource.Platform);
        _db.ChangeTracker.Clear();
    }

    private static Event Subscription(string type, string subscriptionId, string status, OrgEntity org, string priceId) =>
        StripeTestEvents.Subscription(type, subscriptionId, status, org.Id, org.StripeCustomerId!, priceId);
}
