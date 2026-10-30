using System.Text.Json.Nodes;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Features;
using Casazen.Core.Services;
using Casazen.Core.Suppliers;
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

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// SP-13a: the publication of the showcase, its undo and its pause, and the switch of the bookings that follows them, on an in-memory
/// database with the real agenda service (the only writer of <c>OnlineBookingEnabled</c>). The bookings open only with the payment
/// (decision VT-D7): published, not paused, the payments on and the supplier's Stripe account able to take charges and payouts, and
/// they close when the account stops being ready (<c>account.updated</c>). What needs PostgreSQL (the locks, <c>xmin</c>) is a matter of
/// the CI's PostgreSQL run.
/// </summary>
public class SupplierShowcasePublicationTests
{
    private static readonly Guid OrgA = Guid.Parse("00000000-0000-0000-0000-0000000000a1");

    private readonly string _databaseName = Guid.NewGuid().ToString();
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 10, 29, 10, 0, 0, TimeSpan.Zero));
    private readonly List<SupplierServiceListing> _services = [];
    private bool _paymentsFlagOn = true;

    // ─── Publication ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task Publish_EverythingIsReady_PutsTheShowcaseOnline_AndOpensTheBookings()
    {
        await SeedAsync(accountReady: true);
        var service = Build().Service;
        await service.SaveDraftAsync(OrgA, 0, Patch("""{"tagline":"Pronti in un'ora"}"""));

        var view = await service.PublishAsync(OrgA, null);

        Assert.Equal(ShowcaseContentState.Online, view.State);
        Assert.Equal("Pronti in un'ora", view.Published!.Tagline);
        Assert.Empty(view.Changes);
        Assert.True(view.Bookings.Open);
        Assert.True(await SwitchAsync());
        Assert.Equal(_clock.GetUtcNow().UtcDateTime + ShowcaseContentLimits.UndoWindow, view.UndoUntil);
    }

    [Theory]
    [InlineData(false, true, ShowcaseBookingBlocks.AccountNotReady)]
    [InlineData(true, false, ShowcaseBookingBlocks.PaymentsOff)]
    public async Task Publish_WithoutThePaymentsOrTheAccount_IsOnlineButReadOnly_AndSaysWhy(bool accountReady, bool paymentsFlagOn, string reason)
    {
        _paymentsFlagOn = paymentsFlagOn;
        await SeedAsync(accountReady);

        var view = await Build().Service.PublishAsync(OrgA, null);

        Assert.Equal(ShowcaseContentState.Online, view.State);
        Assert.False(view.Bookings.Open);
        Assert.Equal(new[] { reason }, view.Bookings.BlockedBy);
        Assert.False(await SwitchAsync());
    }

    [Fact]
    public async Task Publish_WithoutAnActiveServiceOrTheZones_IsRefusedWithWhatIsMissing_AndNothingIsPublished()
    {
        await SeedAsync(accountReady: true, withService: false, zones: false);

        var refused = await Assert.ThrowsAsync<DomainRuleException>(() => Build().Service.PublishAsync(OrgA, null));

        Assert.Equal(ShowcaseContentErrors.NotPublishable, refused.Code);
        Assert.Equal(new[] { "services", "zones" }, Assert.IsAssignableFrom<IReadOnlyList<string>>(refused.Extensions["fields"]));
        var view = await Build().Service.GetAsync(OrgA);
        Assert.Null(view.Published);
        Assert.Equal(ShowcaseContentState.Draft, view.State);
        Assert.False(await SwitchAsync());
    }

    [Fact]
    public async Task Publish_ASupplierThatIsNotActive_IsRefused()
    {
        await SeedAsync(accountReady: true, status: SupplierStatus.Pending);

        var refused = await Assert.ThrowsAsync<DomainRuleException>(() => Build().Service.PublishAsync(OrgA, null));

        Assert.Equal(ShowcaseContentErrors.SupplierNotActive, refused.Code);
    }

    [Fact]
    public async Task Publish_ADraftOrderNamingAServicePausedMeanwhile_PublishesOnlyTheActiveOnes()
    {
        await SeedAsync(accountReady: true);
        AddService("bucato");
        var service = Build().Service;
        await service.SaveDraftAsync(OrgA, 0, Patch("""{"serviceOrder":["pulizia","bucato"]}"""));
        _services.RemoveAll(s => s.Slug == "bucato");

        var view = await Build().Service.PublishAsync(OrgA, null);

        Assert.Equal(new[] { "pulizia" }, view.Published!.ServiceOrder);
    }

    // ─── Undo, pause, resume ─────────────────────────────────────────────────────

    [Fact]
    public async Task Undo_TheFirstPublication_PutsTheShowcaseBackToADraft_AndTheBookingsClose()
    {
        await SeedAsync(accountReady: true);
        var service = Build().Service;
        await service.SaveDraftAsync(OrgA, 0, Patch("""{"tagline":"Pronti in un'ora"}"""));
        Assert.True((await service.PublishAsync(OrgA, null)).Bookings.Open);

        var undone = await Build().Service.UndoPublishAsync(OrgA);

        Assert.Equal(ShowcaseContentState.Draft, undone.State);
        Assert.Null(undone.Published);
        Assert.Equal("Pronti in un'ora", undone.Draft.Tagline);
        Assert.False(undone.Bookings.Open);
        Assert.False(await SwitchAsync());
    }

    [Fact]
    public async Task Undo_ARepublication_BringsBackThePreviousVersion_UntilTheWindowEnds()
    {
        await SeedAsync(accountReady: true);
        var service = Build().Service;
        await service.SaveDraftAsync(OrgA, 0, Patch("""{"tagline":"Uno"}"""));
        await service.PublishAsync(OrgA, null);
        await Build().Service.SaveDraftAsync(OrgA, 0, Patch("""{"tagline":"Due"}"""));
        Assert.Equal("Due", (await Build().Service.PublishAsync(OrgA, null)).Published!.Tagline);

        var undone = await Build().Service.UndoPublishAsync(OrgA);

        // "Uno" is published again; the draft stays "Due", so there are changes to publish.
        Assert.Equal("Uno", undone.Published!.Tagline);
        Assert.Equal("Due", undone.Draft.Tagline);
        Assert.Equal(ShowcaseContentState.ChangesReady, undone.State);

        // Publish again and let the time pass: the undo is gone.
        await Build().Service.PublishAsync(OrgA, null);
        _clock.Advance(ShowcaseContentLimits.UndoWindow + TimeSpan.FromSeconds(1));
        var late = await Assert.ThrowsAsync<DomainConflictException>(() => Build().Service.UndoPublishAsync(OrgA));
        Assert.Equal(ShowcaseContentErrors.UndoUnavailable, late.Code);
    }

    [Fact]
    public async Task Pause_ClosesTheBookings_AndResumeOpensThemAgain()
    {
        await SeedAsync(accountReady: true);
        await Build().Service.PublishAsync(OrgA, null);

        var paused = await Build().Service.PauseAsync(OrgA);

        Assert.Equal(ShowcaseContentState.Paused, paused.State);
        Assert.Contains(ShowcaseBookingBlocks.Paused, paused.Bookings.BlockedBy);
        Assert.False(await SwitchAsync());

        var resumed = await Build().Service.ResumeAsync(OrgA);

        Assert.Equal(ShowcaseContentState.Online, resumed.State);
        Assert.True(resumed.Bookings.Open);
        Assert.True(await SwitchAsync());
    }

    [Fact]
    public async Task Pause_NothingPublished_IsRefused()
    {
        await SeedAsync(accountReady: true);

        var refused = await Assert.ThrowsAsync<DomainRuleException>(() => Build().Service.PauseAsync(OrgA));

        Assert.Equal(ShowcaseContentErrors.NotPublished, refused.Code);
    }

    [Fact]
    public async Task Resume_OnAShowcaseThatIsNotPaused_OpensTheBookingsOnceTheAccountIsReady()
    {
        await SeedAsync(accountReady: false);
        var published = await Build().Service.PublishAsync(OrgA, null);
        Assert.Equal(new[] { ShowcaseBookingBlocks.AccountNotReady }, published.Bookings.BlockedBy);

        await SetAccountReadyAsync(true);
        var view = await Build().Service.ResumeAsync(OrgA);

        Assert.True(view.Bookings.Open);
        Assert.True(await SwitchAsync());
    }

    // ─── The account of the supplier (account.updated) ───────────────────────────

    [Fact]
    public async Task AccountStopsBeingReady_TheBookingsCloseAndOpenAgain_WithTheWebhook()
    {
        await SeedAsync(accountReady: true);
        var (service, db) = Build();
        await service.PublishAsync(OrgA, null);
        Assert.True(await SwitchAsync());
        var handler = CreateHandler(db, service);

        await handler.HandleEventAsync(AccountUpdated("evt_off", charges: false, payouts: false), WebhookSource.Connected);
        Assert.False(await SwitchAsync());
        Assert.Contains(ShowcaseBookingBlocks.AccountNotReady, (await Build().Service.GetAsync(OrgA)).Bookings.BlockedBy);

        await handler.HandleEventAsync(AccountUpdated("evt_on", charges: true, payouts: true), WebhookSource.Connected);
        Assert.True(await SwitchAsync());
    }

    [Fact]
    public async Task AccountBecomesReady_ASupplierThatNeverPublishedKeepsItsBookingsClosed()
    {
        await SeedAsync(accountReady: false);
        var (service, db) = Build();
        var handler = CreateHandler(db, service);

        await handler.HandleEventAsync(AccountUpdated("evt_ready", charges: true, payouts: true), WebhookSource.Connected);

        Assert.False(await SwitchAsync());
    }

    // ─── helpers ─────────────────────────────────────────────────────────────────

    private static JsonObject Patch(string json) => (JsonObject)JsonNode.Parse(json)!;

    private AppDbContext CreateDb() =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(_databaseName).Options);

    private void AddService(string slug) =>
        _services.Add(new SupplierServiceListing
        {
            OrgId = OrgA,
            Slug = slug,
            Name = slug,
            Category = "cleaning",
            Status = SupplierServiceListingStatus.Active,
            DurationMinutes = 60,
            PriceFromCents = 4500,
        });

    /// <summary>A service on its own context (the real agenda on it), as every request gets its own.</summary>
    private (SupplierShowcaseContentService Service, AppDbContext Db) Build()
    {
        var db = CreateDb();

        var catalog = new Mock<ISupplierServiceCatalogService>();
        catalog
            .Setup(c => c.ListAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => _services.ToList());

        var storage = new Mock<IFileStorage>();
        storage.Setup(s => s.TryGetPublicKey(It.IsAny<string>())).Returns((string?)null);

        var features = new Mock<IFeatureFlags>();
        features.Setup(f => f.IsEnabled(FeatureFlags.SupplierOnlinePayments)).Returns(() => _paymentsFlagOn);

        var agenda = new SupplierAgendaService(
            db, new SupplierServiceRequestReader(db), new ShowcaseHoldReader(db), NullLogger<SupplierAgendaService>.Instance, _clock);
        var service = new SupplierShowcaseContentService(
            db, catalog.Object, agenda, storage.Object, features.Object, NullLogger<SupplierShowcaseContentService>.Instance, _clock);
        return (service, db);
    }

    private async Task SeedAsync(bool accountReady, bool withService = true, bool zones = true, SupplierStatus status = SupplierStatus.Active)
    {
        await using var db = CreateDb();
        db.Orgs.Add(new OrgEntity
        {
            Id = OrgA,
            Name = "Fornitore Srl",
            Slug = "fornitore-srl",
            DisplayName = "Fornitore Srl",
            ContactEmail = "fornitore@example.com",
            OrgType = OrgType.Supplier,
            IsActive = true,
            StripeConnectedAccountId = "acct_fornitore",
            ConnectChargesEnabled = accountReady,
            ConnectPayoutsEnabled = accountReady,
            ConnectDetailsSubmitted = accountReady,
        });
        db.SupplierProfiles.Add(new SupplierProfile
        {
            OrgId = OrgA,
            Status = status,
            LegalName = "Fornitore Srl",
            VatNumber = "IT12345678901",
            Phone = "+39 06 000000",
            Email = "fornitore@example.com",
            ComuniJson = zones ? """["H501"]""" : "[]",
        });
        await db.SaveChangesAsync();

        if (withService)
            AddService("pulizia");
    }

    private async Task SetAccountReadyAsync(bool ready)
    {
        await using var db = CreateDb();
        var org = await db.Orgs.SingleAsync(o => o.Id == OrgA);
        org.ConnectChargesEnabled = ready;
        org.ConnectPayoutsEnabled = ready;
        await db.SaveChangesAsync();
    }

    private async Task<bool> SwitchAsync()
    {
        await using var db = CreateDb();
        return await db.SupplierSettings.AsNoTracking().Where(s => s.OrgId == OrgA).Select(s => s.OnlineBookingEnabled).SingleOrDefaultAsync();
    }

    private static StripeWebhookHandler CreateHandler(AppDbContext db, ISupplierShowcaseContentService showcaseContent) =>
        new(
            new PaymentRepository(db),
            new BookingRepository(db),
            new ConnectOnboardingService(db, Mock.Of<IStripeConnectGateway>(), NullLogger<ConnectOnboardingService>.Instance),
            db,
            Mock.Of<IStripeBillingService>(),
            Mock.Of<IEntitlementService>(),
            Mock.Of<IPlatformInvoiceService>(),
            Mock.Of<IRentBillingService>(),
            Mock.Of<IPaymentRefundService>(),
            TestCheckoutPaymentSettlement.Create(db, null),
            TestDeferredCharges.Create(db),
            Mock.Of<ISupplierPaymentWebhookService>(),
            NullLogger<StripeWebhookHandler>.Instance,
            showcaseContent: showcaseContent);

    private static Event AccountUpdated(string eventId, bool charges, bool payouts) =>
        new()
        {
            Id = eventId,
            Type = "account.updated",
            Account = "acct_fornitore",
            Data = new EventData
            {
                Object = new Account
                {
                    Id = "acct_fornitore",
                    ChargesEnabled = charges,
                    PayoutsEnabled = payouts,
                    DetailsSubmitted = true,
                    Requirements = new AccountRequirements { CurrentlyDue = [] },
                },
            },
        };
}
