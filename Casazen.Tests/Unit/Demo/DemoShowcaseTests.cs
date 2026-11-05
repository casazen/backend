using Casazen.Core.Demo;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Enums;
using Casazen.Core.Options;
using Casazen.Core.Regulatory;
using Casazen.Core.Services;
using Casazen.Core.SiteProfile;
using Xunit;

namespace Casazen.Tests.Unit.Demo;

public class DemoShowcaseTests
{
    [Fact]
    public void Cin_MatchesTheOfficialFormat_AndNamesTheComune_AndSaysTest()
    {
        var cin = DemoCin.ForIstat("058091", 7);

        Assert.Equal("IT058091C2TEST07", cin);
        Assert.True(CinFormat.IsValid(cin));
        Assert.Equal("058091", CinFormat.GetIstatComuneCode(cin));
        Assert.Contains("TEST", cin, StringComparison.Ordinal);
    }

    [Fact]
    public void Document_IsARealPdf_AndSaysItIsTestData()
    {
        var pdf = DemoDocumentPdf.Create("CERTIFICATO CIN DI TEST", DemoDocumentPdf.Marker + " CasaZen");
        var text = System.Text.Encoding.ASCII.GetString(pdf);

        Assert.StartsWith("%PDF", text, StringComparison.Ordinal);
        Assert.Contains("%%EOF", text, StringComparison.Ordinal);
        Assert.Contains(DemoDocumentPdf.Marker, text, StringComparison.Ordinal);
    }

    [Fact]
    public void SafetyChecklist_SatisfiesTheActivationRules()
    {
        var checklist = new PropertySafetyChecklist { OrgId = Guid.NewGuid(), PropertyId = Guid.NewGuid() };
        DemoSafetyChecklist.Apply(checklist, new DateTime(2026, 10, 12, 8, 0, 0, DateTimeKind.Utc));
        foreach (var item in DemoSafetyChecklist.Items(checklist.OrgId, new DateOnly(2026, 10, 12)))
        {
            item.ChecklistId = checklist.Id;
            checklist.Items.Add(item);
        }

        var evaluation = SafetyChecklistRules.Evaluate(checklist);

        Assert.True(evaluation.IsComplete);
        Assert.Empty(evaluation.Blockers);
    }

    [Fact]
    public void RequiredDocuments_FollowTheRegion()
    {
        var configured = new ComplianceOptions().RequiredDocuments;

        Assert.Contains(DocumentType.Ape, DemoRequiredDocuments.ForRegion(configured, "LOM"));
        Assert.Contains(DocumentType.PropertyLicense, DemoRequiredDocuments.ForRegion(configured, "laz"));
        Assert.Equal([DocumentType.CinCertificate], DemoRequiredDocuments.ForRegion(configured, "TOS"));
    }

    [Fact]
    public void Catalog_CoversEveryScenario_WithUniqueKeys()
    {
        var keys = DemoShowcaseCatalog.Properties.Select(p => p.Key).ToList();
        Assert.Equal(keys.Count, keys.Distinct(StringComparer.Ordinal).Count());

        var covered = DemoShowcaseCatalog.Properties
            .SelectMany(p => p.Scenarios.Concat(p.Bookings.Select(b => b.Scenario)))
            .Concat(DemoShowcaseCatalog.HostInvites.Select(i => i.Scenario))
            .Concat(DemoShowcaseCatalog.PropertyOwners.Select(o => o.Scenario))
            .Concat(DemoShowcaseCatalog.PropertyOwnerships.Select(o => o.Scenario))
            .Concat(DemoShowcaseCatalog.GuestSegments.Select(s => s.Scenario))
            .Concat(DemoShowcaseCatalog.Campaigns.Select(c => c.Scenario))
            .Concat(DemoShowcaseCatalog.AgencyInvoices.Select(inv => inv.Scenario))
            .Concat(DemoShowcaseCatalog.StaffMembers.Select(staff => staff.Scenario))
            .Concat(DemoShowcaseCatalog.Suppliers.Select(sup => sup.Scenario))
            .Concat(DemoShowcaseCatalog.CheckIns.Select(ci => ci.Scenario))
            .Concat(DemoShowcaseCatalog.AlloggiatiReports.Select(ar => ar.Scenario))
            .Concat(DemoShowcaseCatalog.Statements.Select(st => st.Scenario))
            .Concat(DemoShowcaseCatalog.WithholdingSummaries.Select(wh => wh.Scenario))
            .Concat(DemoShowcaseCatalog.F24Records.Select(f24 => f24.Scenario))
            .Concat(DemoShowcaseCatalog.WireBatches.Select(wb => wb.Scenario))
            .Concat(DemoShowcaseCatalog.Routines.Select(rt => rt.Scenario))
            .Concat(DemoShowcaseCatalog.PopupAlerts.Select(alert => alert.Scenario))
            .ToHashSet(StringComparer.Ordinal);

        Assert.All(DemoShowcaseCatalog.Scenarios, scenario => Assert.Contains(scenario, covered));
        Assert.Contains(DemoShowcaseCatalog.Properties, p => p.Publish);
        Assert.Contains(DemoShowcaseCatalog.Properties, p => p.Key == DemoShowcaseCatalog.DraftKey && !p.Publish);
        Assert.All(DemoShowcaseCatalog.Properties, p => Assert.Equal(6, p.Place.IstatCode.Length));
    }

    [Fact]
    public void HostInvites_EveryTemplate_IsObviouslyTestData_WithUniqueKeys_AndEveryStateIsCovered()
    {
        var invites = DemoShowcaseCatalog.HostInvites;

        Assert.Equal(invites.Count, invites.Select(i => i.Key).Distinct(StringComparer.Ordinal).Count());
        Assert.All(invites, i => Assert.EndsWith("@example.com", i.Email, StringComparison.Ordinal));
        Assert.All(invites, i => Assert.True(i.CreatedOffsetDays < 0));
        Assert.Equal(
            Enum.GetValues<DemoHostInviteState>().Order(),
            invites.Select(i => i.State).Distinct().Order());
        Assert.Contains(DemoShowcaseCatalog.Scenario.HostInvitePending, DemoShowcaseCatalog.Scenarios);
    }

    [Fact]
    public void GuestAppAccount_TheDemoGuestOfTheMobileApp_HasUpcomingPastAndCancelledStaysOfThePublicCheckout()
    {
        var stays = DemoShowcaseCatalog.Properties
            .SelectMany(p => p.Bookings.Where(b => b.Scenario == DemoShowcaseCatalog.Scenario.GuestAppAccount))
            .ToList();

        Assert.Contains(DemoShowcaseCatalog.Scenario.GuestAppAccount, DemoShowcaseCatalog.Scenarios);
        // Obviously test data, and the email of the account the store reviewers sign in with (created by hand in Auth0).
        Assert.EndsWith("@example.com", DemoShowcaseCatalog.GuestAppEmail, StringComparison.Ordinal);
        Assert.All(stays, s => Assert.Equal(DemoShowcaseCatalog.GuestAppEmail, s.Email));
        // «Le mie prenotazioni» of the app lists the bookings of the public checkout only, and the demo must not reach Stripe: paid at the property.
        Assert.All(stays, s => Assert.Equal(BookingSource.Direct, s.Source));
        Assert.All(stays, s => Assert.Equal(PaymentOption.OnSite, s.PaymentOption));
        Assert.Equal(stays.Count, stays.Select(s => s.Key).Distinct(StringComparer.Ordinal).Count());
        Assert.Contains(stays, s => s.CheckInOffsetDays > 0 && s.Status == BookingStatus.Confirmed);
        Assert.Contains(stays, s => s.CheckInOffsetDays < 0 && s.Status == BookingStatus.CheckedOut);
        Assert.Contains(stays, s => s.Status == BookingStatus.Cancelled && s.CancellationReason == BookingCancellationReason.GuestRequested);
    }

    [Fact]
    public void ReportYearOverYear_TheDemoHasFinishedStaysAboutOneYearAgo_OnSeveralChannels()
    {
        var stays = DemoShowcaseCatalog.Properties
            .SelectMany(p => p.Bookings.Where(b => b.Scenario == DemoShowcaseCatalog.Scenario.ReportYearOverYear))
            .ToList();

        // NF-96: the same months of the year before the other demo stays are in, so the comparison of the reports is never empty.
        Assert.Contains(DemoShowcaseCatalog.Scenario.ReportYearOverYear, DemoShowcaseCatalog.Scenarios);
        Assert.NotEmpty(stays);
        Assert.All(stays, s => Assert.EndsWith("@example.com", s.Email, StringComparison.Ordinal));
        Assert.All(stays, s => Assert.Equal(BookingStatus.CheckedOut, s.Status));
        Assert.All(stays, s => Assert.InRange(s.CheckInOffsetDays, -400, -300));
        Assert.Equal(stays.Count, stays.Select(s => s.Key).Distinct(StringComparer.Ordinal).Count());
        Assert.True(stays.Select(s => s.Source).Distinct().Count() >= 3, "A comparison by channel needs several channels.");
        Assert.True(
            DemoShowcaseCatalog.Properties.Count(p => p.Bookings.Any(b => b.Scenario == DemoShowcaseCatalog.Scenario.ReportYearOverYear)) >= 2,
            "A comparison by property needs more than one property.");
    }

    [Fact]
    public void Catalog_NoDemoProperty_HasTwoStaysOnTheSameNight()
    {
        foreach (var property in DemoShowcaseCatalog.Properties)
        {
            var taken = property.Bookings
                .Where(b => b.Status != BookingStatus.Cancelled)
                .OrderBy(b => b.CheckInOffsetDays)
                .ToList();
            for (var index = 1; index < taken.Count; index++)
            {
                var before = taken[index - 1];
                Assert.True(
                    before.CheckInOffsetDays + before.Nights <= taken[index].CheckInOffsetDays,
                    $"{property.Key}: the stays {before.Key} and {taken[index].Key} take the same night.");
            }
        }
    }

    [Fact]
    public void PropertyOwnerships_TheSharesOfEachDemoProperty_AddUpToOneHundred_AndOneHouseIsCoOwned()
    {
        var ownerships = DemoShowcaseCatalog.PropertyOwnerships;
        var owners = DemoShowcaseCatalog.PropertyOwners.Select(o => o.Key).ToHashSet(StringComparer.Ordinal);
        var properties = DemoShowcaseCatalog.Properties.Select(p => p.Key).ToHashSet(StringComparer.Ordinal);

        Assert.All(ownerships, o =>
        {
            Assert.Contains(o.OwnerKey, owners);
            Assert.Contains(o.PropertyKey, properties);
            Assert.Contains(o.Scenario, DemoShowcaseCatalog.Scenarios);
            // The share of an ownership is the one the owner's template says.
            Assert.Equal(DemoShowcaseCatalog.PropertyOwners.Single(x => x.Key == o.OwnerKey).OwnershipSharePercent, o.SharePercent);
        });
        Assert.All(ownerships.GroupBy(o => o.PropertyKey), house => Assert.Equal(100m, house.Sum(o => o.SharePercent)));

        // NF-96: the owner of the agency demo has a house with stays, and one house is shared 50/50 by two owners, so the weighting shows.
        var agencyOwner = Assert.Single(ownerships, o => o.Scenario == DemoShowcaseCatalog.Scenario.PropertyOwnerAgency);
        Assert.Contains(
            DemoShowcaseCatalog.Properties.Single(p => p.Key == agencyOwner.PropertyKey).Bookings,
            b => b.Status == BookingStatus.CheckedOut);
        var shared = ownerships.Where(o => o.Scenario == DemoShowcaseCatalog.Scenario.OwnerShares).ToList();
        Assert.Equal(2, shared.Count);
        Assert.All(shared, o => Assert.Equal(50m, o.SharePercent));
        Assert.Single(shared.Select(o => o.PropertyKey).Distinct(StringComparer.Ordinal));
    }

    [Fact]
    public void PropertySiteProfile_TheDemoHouseHasItsHeadlineAndDescriptionInEnglish_SoItCanBePublished()
    {
        var house = Assert.Single(
            DemoShowcaseCatalog.Properties,
            p => p.Scenarios.Contains(DemoShowcaseCatalog.Scenario.PropertySiteProfile));
        var english = house.SiteProfile?.Translations?["en"];

        Assert.False(string.IsNullOrWhiteSpace(english?.Title));
        Assert.False(string.IsNullOrWhiteSpace(english?.Description));
        Assert.True(english!.Title!.Length <= SitePropertyProfileRules.ListingTextMaxLength(ListingTextField.Title));
    }

    [Fact]
    public void PropertyRoomsAndDiscounts_TheDemoHouseHasValidRoomsMatchingItsCounters_AndAWeekAndAMonthDiscount()
    {
        var house = Assert.Single(
            DemoShowcaseCatalog.Properties,
            p => p.Scenarios.Contains(DemoShowcaseCatalog.Scenario.PropertyRoomsAndDiscounts));

        var rooms = PropertyRoomRules.Normalize([.. (house.Rooms ?? []).Select(room => new RoomInput(null, room.Type, room.Beds))]);
        var tiers = StayDiscountRules.Normalize([.. (house.StayDiscounts ?? []).Select(tier => new StayDiscountTier(tier.MinNights, tier.PercentOff))]);

        Assert.Contains(DemoShowcaseCatalog.Scenario.PropertyRoomsAndDiscounts, DemoShowcaseCatalog.Scenarios);
        // The counters of the property are what the rooms give, as the rooms service sets them.
        Assert.Equal(house.Bedrooms, rooms.Bedrooms);
        Assert.Equal(house.Profile!.Beds, rooms.Beds);
        Assert.Equal([7, 28], tiers.Select(tier => tier.MinNights));
        Assert.Equal(ChildrenPolicy.Yes, house.Profile.Children);
    }

    [Fact]
    public void PopupAlerts_TheDemoHostHasOneOpenAndOneSnoozedAlert_AboutStaysAndHousesOfTheShowcase()
    {
        var alerts = DemoShowcaseCatalog.PopupAlerts;
        var houses = DemoShowcaseCatalog.Properties.ToDictionary(p => p.Key, StringComparer.Ordinal);

        // NF-08, ADR-025: the queue of the demo host has an alert pending at once and one that comes back later.
        Assert.Contains(DemoShowcaseCatalog.Scenario.PopupAlerts, DemoShowcaseCatalog.Scenarios);
        Assert.Equal(alerts.Count, alerts.Select(a => a.Key).Distinct(StringComparer.Ordinal).Count());
        Assert.Single(alerts, a => a.SnoozedForHours is null);
        Assert.Single(alerts, a => a.SnoozedForHours > 0);
        Assert.All(alerts, a => Assert.Equal(DemoShowcaseCatalog.Scenario.PopupAlerts, a.Scenario));
        Assert.All(alerts, a => Assert.True(Enum.IsDefined(a.Type) && Enum.IsDefined(a.Urgency)));

        // A reference only: the house or the stay it points at exists in the template, and the seed finds it by that key.
        Assert.All(alerts, a =>
        {
            Assert.True(houses.TryGetValue(a.PropertyKey, out var house), $"{a.Key}: no demo property {a.PropertyKey}.");
            if (a.BookingKey is not null)
                Assert.Contains(house!.Bookings, b => b.Key == a.BookingKey);
        });
    }

    [Fact]
    public void ListingChannelStates_TheDemoHouseShowsPublishedToFixAndManagedOnChannel_WithAnErrorOnlyWhereThereIsOneToFix()
    {
        var house = Assert.Single(
            DemoShowcaseCatalog.Properties,
            p => p.Scenarios.Contains(DemoShowcaseCatalog.Scenario.ListingChannelStates));
        var states = house.ChannelStates ?? [];

        Assert.Contains(DemoShowcaseCatalog.Scenario.ListingChannelStates, DemoShowcaseCatalog.Scenarios);
        Assert.Equal(states.Count, states.Select(s => s.ChannelCode).Distinct(StringComparer.Ordinal).Count());
        Assert.Contains(states, s => s.Status == ChannelPublishStatus.Published);
        Assert.Contains(states, s => s.Status == ChannelPublishStatus.ToFix && s.ErrorCode is not null && s.ErrorMessage is not null);
        Assert.Contains(states, s => s.Status == ChannelPublishStatus.ManagedOnChannel);
        Assert.All(states.Where(s => s.Status != ChannelPublishStatus.ToFix), s => Assert.Null(s.ErrorCode));
    }
}
