using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Features;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Services;
using Casazen.Web.Resources;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Marketplace;

/// <summary>
/// E09/F01/US01/T03, NF-54: the proofs of the catalog and of the Active tab. Permission rule: a partner that is hidden, or that the plan or the
/// country of the org does not allow, is never returned, and an org with no country sees only what has no country restriction. Data rule: what
/// another org activated never reaches the statuses of the cards or the Active list. The database is in memory and the tenant filter is off (a
/// job's context), so what keeps the orgs apart is the service itself.
/// </summary>
public class MarketplaceCatalogTests
{
    [Fact]
    public async Task MarketplaceCatalog_PartnerVisibility_ByPlanAndCountry_Test()
    {
        await using var db = NewDb();
        var starterInItaly = await AddOrgAsync(db, PlanTier.Starter, "IT");
        var proInSwitzerland = await AddOrgAsync(db, PlanTier.Pro, "ch"); // the country is compared without regard to case
        var proWithoutCountry = await AddOrgAsync(db, PlanTier.Pro, null);

        var partners = new[]
        {
            AddPartner(db, "open"),
            AddPartner(db, "pro-only", plans: [PlanTier.Pro]),
            AddPartner(db, "italy-only", countries: ["IT"]),
            AddPartner(db, "pro-in-switzerland", plans: [PlanTier.Pro], countries: ["CH"]),
            AddPartner(db, "hidden", visible: false),
        };
        await db.SaveChangesAsync();
        var service = NewService(db);

        // Every org gets the five groups in the order of the page, and the Partners group holds only the partners its plan and country allow.
        Assert.Equal(new[] { "italy-only", "open" }, await PartnerCodesAsync(service, starterInItaly));
        Assert.Equal(new[] { "open", "pro-in-switzerland", "pro-only" }, await PartnerCodesAsync(service, proInSwitzerland));

        // No country on file: a restriction is never lifted, so the org sees what has none (fail closed).
        Assert.Equal(new[] { "open", "pro-only" }, await PartnerCodesAsync(service, proWithoutCountry));

        var catalog = await service.GetCatalogAsync(starterInItaly, new HostScope(starterInItaly), null, "it");
        Assert.Equal(MarketplaceGroupIds.InOrder, catalog.Groups.Select(group => group.Id).ToList());

        // The pure rule says the same, and a hidden partner is hidden for everybody.
        var proOnly = partners.Single(p => p.Code == "pro-only");
        Assert.False(MarketplaceVisibility.IsVisible(proOnly, PlanTier.Starter, "IT"));
        Assert.True(MarketplaceVisibility.IsVisible(proOnly, PlanTier.Pro, null));
        var italyOnly = partners.Single(p => p.Code == "italy-only");
        Assert.True(MarketplaceVisibility.IsVisible(italyOnly, PlanTier.Scale, " it "));
        Assert.False(MarketplaceVisibility.IsVisible(italyOnly, PlanTier.Scale, "CH"));
        Assert.False(MarketplaceVisibility.IsVisible(italyOnly, PlanTier.Scale, null));
        Assert.False(MarketplaceVisibility.IsVisible(italyOnly, PlanTier.Scale, "  "));
        var hidden = partners.Single(p => p.Code == "hidden");
        Assert.All(
            Enum.GetValues<PlanTier>(),
            plan => Assert.False(MarketplaceVisibility.IsVisible(hidden, plan, "IT")));
    }

    [Fact]
    public async Task MarketplaceCatalog_OtherOrgActivations_NeverShown_Test()
    {
        await using var db = NewDb();
        var orgA = await AddOrgAsync(db, PlanTier.Pro, "IT");
        var orgB = await AddOrgAsync(db, PlanTier.Pro, "IT");
        var propertyB = await AddPropertyAsync(db, orgB);
        AddPortal(db, "portal-one");
        AddPartner(db, "partner-one");

        // Org B activated a channel for its property, a partner it declared, and a function of the license. Org A activated nothing.
        AddActivation(db, orgB, propertyB, MarketplaceActivationKind.Channel, "portal-one", MarketplaceActivationStatus.Active);
        AddActivation(db, orgB, null, MarketplaceActivationKind.Partner, "partner-one", MarketplaceActivationStatus.Active, declared: true);
        AddActivation(db, orgB, null, MarketplaceActivationKind.LicensedFunction, "chat", MarketplaceActivationStatus.Active);
        await db.SaveChangesAsync();
        var service = NewService(db);

        // Org A sees every card as Not active, counts none, and its Active tab holds no integration.
        var asA = await service.GetCatalogAsync(orgA, new HostScope(orgA), null, "it");
        var cardsOfA = asA.Groups.SelectMany(group => group.Entries).ToList();
        Assert.Contains(cardsOfA, card => card.Code == "portal-one");
        Assert.Contains(cardsOfA, card => card.Code == "partner-one");
        Assert.Contains(cardsOfA, card => card.Code == "chat");
        Assert.All(cardsOfA, card =>
        {
            Assert.Equal(MarketplaceCardStatus.NotActive, card.Status);
            Assert.Null(card.PropertyCount);
        });
        Assert.Empty((await service.GetActiveAsync(orgA, new HostScope(orgA), null)).Integrations);

        // The control: org B reads its own rows, so the check above is not an empty test.
        var asB = await service.GetCatalogAsync(orgB, new HostScope(orgB), null, "it");
        var cardsOfB = asB.Groups.SelectMany(group => group.Entries).ToList();
        var channelOfB = Assert.Single(cardsOfB, card => card.Code == "portal-one");
        Assert.Equal(MarketplaceCardStatus.Active, channelOfB.Status);
        Assert.Equal(1, channelOfB.PropertyCount);
        Assert.Equal(MarketplaceCardStatus.Active, Assert.Single(cardsOfB, card => card.Code == "partner-one").Status);
        Assert.Equal(MarketplaceCardStatus.Active, Assert.Single(cardsOfB, card => card.Code == "chat").Status);
        var integrationsOfB = (await service.GetActiveAsync(orgB, new HostScope(orgB), null)).Integrations;
        Assert.Equal(3, integrationsOfB.Count);
        Assert.True(Assert.Single(integrationsOfB, integration => integration.Kind == MarketplaceActivationKind.Partner).IsDeclared);

        // Org A cannot reach org B's property by naming it, and a scope of another org is refused before anything is read.
        await Assert.ThrowsAsync<NotFoundException>(() => service.GetCatalogAsync(orgA, new HostScope(orgA), propertyB, "it"));
        await Assert.ThrowsAsync<NotFoundException>(() => service.GetActiveAsync(orgA, new HostScope(orgA), propertyB));
        await Assert.ThrowsAsync<ArgumentException>(() => service.GetCatalogAsync(orgA, new HostScope(orgB), null, "it"));
    }

    private static async Task<List<string>> PartnerCodesAsync(MarketplaceCatalogService service, Guid orgId)
    {
        var catalog = await service.GetCatalogAsync(orgId, new HostScope(orgId), null, "it");
        return catalog.Groups.Single(group => group.Id == MarketplaceGroupIds.Partners).Entries.Select(entry => entry.Code).Order().ToList();
    }

    private static AppDbContext NewDb() =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase($"marketplace-catalog-{Guid.NewGuid()}").Options);

    private static MarketplaceCatalogService NewService(AppDbContext db)
    {
        var suppliers = new Mock<IHostSupplierUsageReader>();
        suppliers
            .Setup(s => s.ListAsync(It.IsAny<HostScope>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<HostUsedSupplier>());
        var host = new Mock<IHostEnvironment>();
        host.SetupGet(h => h.EnvironmentName).Returns("Staging");

        // The Octorate connector is off, so the portals and the license functions answer and say they cannot be activated yet.
        return new MarketplaceCatalogService(
            db, suppliers.Object, Mock.Of<IFeatureFlags>(), Mock.Of<IFileStorage>(), new ResxMarketplaceTexts(), host.Object);
    }

    private static async Task<Guid> AddOrgAsync(AppDbContext db, PlanTier plan, string? country)
    {
        var org = new OrgEntity
        {
            Name = "Org " + Guid.NewGuid().ToString("N")[..6],
            Slug = "org-" + Guid.NewGuid().ToString("N"),
            DisplayName = "Org",
            PlanTier = plan,
            BillingCountry = country,
        };
        db.Orgs.Add(org);
        await db.SaveChangesAsync();
        return org.Id;
    }

    private static async Task<Guid> AddPropertyAsync(AppDbContext db, Guid orgId)
    {
        var property = new Property { OrgId = orgId, OwnerId = "auth0|owner", Name = "Casa", Address = "Via Prova 1", City = "Como", MaxGuests = 4 };
        db.Properties.Add(property);
        await db.SaveChangesAsync();
        return property.Id;
    }

    private static MarketplacePartner AddPartner(
        AppDbContext db, string code, bool visible = true, PlanTier[]? plans = null, string[]? countries = null)
    {
        var partner = new MarketplacePartner
        {
            Code = code,
            Name = "Partner " + code,
            IsVisible = visible,
            WebsiteUrl = "https://example.com/",
            VisiblePlanTiers = plans ?? [],
            VisibleCountries = [.. countries ?? []],
        };
        db.MarketplacePartners.Add(partner);
        return partner;
    }

    private static void AddPortal(AppDbContext db, string code) =>
        db.MarketplacePortals.Add(new MarketplacePortal
        {
            PortalCode = code,
            Name = "Portale " + code,
            Category = MarketplacePortalCategory.Ota,
            Creatable = true,
            Enabled = true,
        });

    private static void AddActivation(
        AppDbContext db,
        Guid orgId,
        Guid? propertyId,
        MarketplaceActivationKind kind,
        string target,
        MarketplaceActivationStatus status,
        bool declared = false) =>
        db.MarketplaceActivations.Add(new MarketplaceActivation
        {
            OrgId = orgId,
            PropertyId = propertyId,
            Kind = kind,
            TargetCode = target,
            Status = status,
            IsDeclared = declared,
        });
}
