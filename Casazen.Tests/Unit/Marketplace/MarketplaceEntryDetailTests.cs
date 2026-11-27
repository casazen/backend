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
/// E09/F01/US01/T04, NF-55: the proof of the detail card. Permission rule: a partner that the org may not see (hidden, or not for its plan or
/// its country) cannot be read by guessing its code: the answer is the 404 of a code nobody has, the same for all of them, so it tells nothing. An
/// entry that goes through Octorate does not exist either while the connector is off. The database is in memory: what decides is the service.
/// </summary>
public class MarketplaceEntryDetailTests
{
    [Fact]
    public async Task MarketplaceEntryDetail_HiddenPartner_Returns404_Test()
    {
        await using var db = NewDb();
        var starterInItaly = await AddOrgAsync(db, PlanTier.Starter, "IT");
        var proInSwitzerland = await AddOrgAsync(db, PlanTier.Pro, "CH");

        AddPartner(db, "open");
        AddPartner(db, "hidden", visible: false);
        AddPartner(db, "pro-only", plans: [PlanTier.Pro]);
        AddPartner(db, "switzerland-only", countries: ["CH"]);
        db.MarketplacePortals.Add(new MarketplacePortal
        {
            PortalCode = "portal-one",
            Name = "Portale uno",
            Category = MarketplacePortalCategory.Ota,
            Creatable = true,
            Enabled = true,
            RequiresHotelId = true,
            RequiresTokenAuthentication = true,
        });
        await db.SaveChangesAsync();
        var service = NewService(db, octorateConnector: false);
        var scopeOfStarter = new HostScope(starterInItaly);

        // The partner everybody may see is read, in Italian when the language asked for has no text of its own.
        var open = await service.GetAsync(starterInItaly, scopeOfStarter, MarketplaceActivationKind.Partner, "open", null, "en");
        Assert.Equal("Descrizione di open", open.WhatItDoes);
        Assert.Equal(MarketplaceActivationTypes.PartnerSite, open.Activation.Type);
        Assert.Null(open.Cost.Text);

        // Guessing the code of a partner the org may not see is the same 404 as a code nobody has.
        foreach (var code in new[] { "hidden", "pro-only", "switzerland-only", "nobody-has-this-code" })
        {
            var error = await Assert.ThrowsAsync<NotFoundException>(
                () => service.GetAsync(starterInItaly, scopeOfStarter, MarketplaceActivationKind.Partner, code, null, "it"));
            Assert.Equal("marketplace_entry_not_found", error.Code);
        }

        // The control: the org the plan and the country allow reads the partners reserved to them, and nobody reads the hidden one.
        var scopeOfPro = new HostScope(proInSwitzerland);
        Assert.Equal("pro-only", (await service.GetAsync(proInSwitzerland, scopeOfPro, MarketplaceActivationKind.Partner, "pro-only", null, "it")).Code);
        Assert.Equal(
            "switzerland-only",
            (await service.GetAsync(proInSwitzerland, scopeOfPro, MarketplaceActivationKind.Partner, "switzerland-only", null, "it")).Code);
        await Assert.ThrowsAsync<NotFoundException>(
            () => service.GetAsync(proInSwitzerland, scopeOfPro, MarketplaceActivationKind.Partner, "hidden", null, "it"));

        // What goes through Octorate does not exist while the connector is off, and exists with it on, its requirements following the flags of the portal.
        await Assert.ThrowsAsync<NotFoundException>(
            () => service.GetAsync(starterInItaly, scopeOfStarter, MarketplaceActivationKind.Channel, "portal-one", null, "it"));
        await Assert.ThrowsAsync<NotFoundException>(
            () => service.GetAsync(starterInItaly, scopeOfStarter, MarketplaceActivationKind.LicensedFunction, "chat", null, "it"));

        var connected = NewService(db, octorateConnector: true);
        var channel = await connected.GetAsync(starterInItaly, scopeOfStarter, MarketplaceActivationKind.Channel, "portal-one", null, "it");
        Assert.Equal(new[] { "hotelId", "panelAccess" }, channel.Activation.Requirements.Select(requirement => requirement.Code));
        Assert.True(channel.Activation.NeedsOctoratePanel);
        Assert.Equal(MarketplaceActivationTypes.Api, channel.Activation.Type);
        Assert.Null(channel.Cost.Text);
    }

    private static AppDbContext NewDb() =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase($"marketplace-detail-{Guid.NewGuid()}").Options);

    private static MarketplaceEntryDetailService NewService(AppDbContext db, bool octorateConnector)
    {
        var flags = new Mock<IFeatureFlags>();
        flags.Setup(f => f.IsEnabled(FeatureFlags.OctorateConnector)).Returns(octorateConnector);
        var host = new Mock<IHostEnvironment>();
        host.SetupGet(h => h.EnvironmentName).Returns("Staging");
        return new MarketplaceEntryDetailService(db, flags.Object, Mock.Of<IFileStorage>(), new ResxMarketplaceTexts(), host.Object);
    }

    private static async Task<Guid> AddOrgAsync(AppDbContext db, PlanTier plan, string country)
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

    private static void AddPartner(
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
        partner.Texts.Add(new MarketplacePartnerText
        {
            PartnerId = partner.Id,
            Language = MarketplacePartnerText.Italian,
            Benefit = "Beneficio di " + code,
            Audience = "Per tutti",
            Description = "Descrizione di " + code,
            Requirements = "Primo requisito\nSecondo requisito",
            DataShared = "Nessun dato",
            Deactivation = "Si disattiva sul sito del partner",
            ActivationGuide = "Guida",
            FaqJson = """[{"q":"Domanda?","a":"Risposta."}]""",
        });
        db.MarketplacePartners.Add(partner);
    }
}
