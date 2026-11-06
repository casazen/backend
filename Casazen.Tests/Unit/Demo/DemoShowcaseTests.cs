using System.Text.Json;
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
            .Concat(DemoShowcaseCatalog.WizardDrafts.Select(wd => wd.Scenario))
            .Concat(DemoShowcaseCatalog.TouristTaxRemittances.Select(tt => tt.Scenario))
            .Concat(DemoShowcaseCatalog.MarketplacePartners.Select(partner => partner.Scenario))
            .Concat(DemoShowcaseCatalog.MarketplaceActivations.Select(activation => activation.Scenario))
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
    public void WizardDrafts_TheDemoDraft_IsObviouslyTestData_InsideItsThirtyDays_AndHasPersonalDataToEncrypt()
    {
        var drafts = DemoShowcaseCatalog.WizardDrafts;

        Assert.Contains(DemoShowcaseCatalog.Scenario.WizardDrafts, DemoShowcaseCatalog.Scenarios);
        Assert.NotEmpty(drafts);
        Assert.Equal(drafts.Count, drafts.Select(d => d.Key).Distinct(StringComparer.Ordinal).Count());
        // One draft per wizard: the demo host has a single draft of a new entity for each one (the unique index of the table).
        Assert.Equal(drafts.Count, drafts.Select(d => d.Type).Distinct().Count());
        Assert.All(drafts, draft =>
        {
            Assert.True(Enum.IsDefined(draft.Type));
            // Saved in the past but inside the 30 days, so the retention does not anonymize it the night after a reset.
            Assert.InRange(draft.SavedOffsetDays, 1 - WizardDraftLimits.RetentionDays, 0);

            using var data = JsonDocument.Parse(draft.DataJson);
            var split = WizardDraftPii.Split(data.RootElement);

            // The personal half exists, so the demo walks through the encryption; what is in it is invented (example.com, TEST).
            Assert.NotNull(split.PersonalJson);
            Assert.Contains("@example.com", split.PersonalJson, StringComparison.Ordinal);
            Assert.Contains("TEST", split.PersonalJson, StringComparison.Ordinal);
            Assert.DoesNotContain("@", split.PlainJson, StringComparison.Ordinal);
        });
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
    public void TouristTaxPaid_TheDemoHasStaysThatCarryTaxAndAPaymentOfPartOfIt_ValidForTheRulesOfARealPayment()
    {
        var payment = Assert.Single(DemoShowcaseCatalog.TouristTaxRemittances);
        var house = Assert.Single(DemoShowcaseCatalog.Properties, p => p.Key == payment.PropertyKey);
        var stays = house.Bookings.Where(b => b.Scenario == DemoShowcaseCatalog.Scenario.TouristTaxPaid).ToList();
        var taxed = stays.Where(s => (s.TouristTax ?? 0m) > 0m).ToList();

        // NF-100: the summary of the demo host shows an amount owed, a part of it paid with a receipt, and a stay counted apart.
        Assert.Contains(DemoShowcaseCatalog.Scenario.TouristTaxPaid, DemoShowcaseCatalog.Scenarios);
        Assert.Equal(DemoShowcaseCatalog.Scenario.TouristTaxPaid, payment.Scenario);
        Assert.Equal(stays.Count, stays.Select(s => s.Key).Distinct(StringComparer.Ordinal).Count());
        Assert.All(stays, s => Assert.EndsWith("@example.com", s.Email, StringComparison.Ordinal));
        // The tourist tax report lists confirmed, checked-in and checked-out stays, and these are all finished.
        Assert.All(stays, s => Assert.Equal(BookingStatus.CheckedOut, s.Status));
        Assert.All(stays, s => Assert.True(s.CheckInOffsetDays + s.Nights <= 0, $"{s.Key} is not over."));
        Assert.True(taxed.Count >= 2, "The comune needs two stays that carry tax: one paid and one still to pay.");
        Assert.Contains(stays, s => (s.TouristTax ?? 0m) == 0m);

        // The payment obeys the rules of a payment recorded through the service: an amount above zero, a period that is not backwards and
        // lasts at most a year, a day that is not in the future.
        var anchor = new DateOnly(2026, 11, 6);
        Assert.True(TouristTaxSummaryRules.IsValidAmount(payment.PaidAmount));
        Assert.True(TouristTaxSummaryRules.IsValidRemittancePeriod(
            anchor.AddDays(payment.PeriodFromOffsetDays), anchor.AddDays(payment.PeriodToOffsetDays)));
        Assert.True(payment.PaidOnOffsetDays <= 0);
        Assert.Contains("TEST", payment.Reference, StringComparison.Ordinal);

        // It pays what the stays that check in during its period owe, not more, and leaves another stay to pay.
        var inside = taxed
            .Where(s => s.CheckInOffsetDays >= payment.PeriodFromOffsetDays && s.CheckInOffsetDays <= payment.PeriodToOffsetDays)
            .ToList();
        Assert.NotEmpty(inside);
        Assert.Equal(payment.PaidAmount, inside.Sum(s => s.TouristTax ?? 0m));
        Assert.True(taxed.Count > inside.Count);
    }

    [Fact]
    public void MarketplacePartners_TheDemoPartner_IsObviouslyTestData_InBothLanguages_AndTheHostDeclaredItActivatedInThePast()
    {
        var partners = DemoShowcaseCatalog.MarketplacePartners;
        var activations = DemoShowcaseCatalog.MarketplaceActivations;

        // E09/F01, NF-54, NF-56: the Partners group of a test environment has a card, and the Active tab a declared activation of it.
        Assert.Contains(DemoShowcaseCatalog.Scenario.MarketplacePartnersVisible, DemoShowcaseCatalog.Scenarios);
        Assert.Contains(DemoShowcaseCatalog.Scenario.MarketplaceActivationDeclared, DemoShowcaseCatalog.Scenarios);
        Assert.NotEmpty(partners);
        Assert.NotEmpty(activations);
        Assert.Equal(partners.Count, partners.Select(p => p.Key).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(partners.Count, partners.Select(p => p.Code).Distinct(StringComparer.Ordinal).Count());
        Assert.All(partners, partner =>
        {
            // Named TEST, pointing at an example address, a lowercase slug as the table requires, texts in both languages that say it is a test.
            Assert.Contains("TEST", partner.Name, StringComparison.Ordinal);
            Assert.Equal("https://example.com/", partner.WebsiteUrl);
            Assert.Matches("^[a-z0-9]+(-[a-z0-9]+)*$", partner.Code);
            Assert.True(partner.Code.Length <= MarketplacePartner.CodeMaxLength && partner.Name.Length <= MarketplacePartner.NameMaxLength);
            Assert.True(Enum.IsDefined(partner.Job) && Enum.IsDefined(partner.InvoicedBy));
            Assert.Equal(DemoShowcaseCatalog.Scenario.MarketplacePartnersVisible, partner.Scenario);
            foreach (var text in new[] { partner.Italian, partner.English })
            {
                Assert.InRange(text.Benefit.Length, 1, MarketplacePartnerText.BenefitMaxLength);
                Assert.Contains("TEST", text.Benefit, StringComparison.Ordinal);
                Assert.NotEmpty(text.Faq);
            }

            Assert.NotEqual(partner.Italian.Benefit, partner.English.Benefit);
        });

        // The activation names a partner of the template and was declared before the reset, never in the future.
        Assert.All(activations, activation =>
        {
            Assert.Contains(partners, partner => partner.Key == activation.PartnerKey);
            Assert.True(activation.DeclaredOffsetDays < 0, $"{activation.Key} is not declared in the past.");
            Assert.Equal(DemoShowcaseCatalog.Scenario.MarketplaceActivationDeclared, activation.Scenario);
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

    [Fact]
    public void CrmProfiles_TheDemoHasAReturningGuestAnOtaGuestADirectCustomerACheckInConsentAndTwoGuestsSharingAPhone_AllInvented()
    {
        var stays = DemoShowcaseCatalog.Properties
            .SelectMany(p => p.Bookings.Where(b => b.Scenario == DemoShowcaseCatalog.Scenario.CrmProfiles).Select(b => (House: p.Key, Booking: b)))
            .ToList();

        // NF-88, NF-89: the CRM sync builds the profiles from these bookings, so the scenario has template data and every case it shows is in it.
        Assert.Contains(DemoShowcaseCatalog.Scenario.CrmProfiles, DemoShowcaseCatalog.Scenarios);
        Assert.NotEmpty(stays);
        Assert.Equal(stays.Count, stays.Select(s => s.Booking.Key).Distinct(StringComparer.Ordinal).Count());
        Assert.All(stays, s => Assert.EndsWith("@example.com", s.Booking.Email, StringComparison.Ordinal));
        Assert.All(stays, s => Assert.NotEqual(BookingStatus.Cancelled, s.Booking.Status));
        Assert.Contains("TEST", DemoShowcaseCatalog.CrmTextVersion, StringComparison.Ordinal);

        // A returning guest: the same address on two records, a stay that is over and one to come, at two houses.
        var returning = Assert.Single(
            stays.GroupBy(s => s.Booking.Email, StringComparer.Ordinal),
            group => group.Count() == 2 && group.Select(s => s.House).Distinct(StringComparer.Ordinal).Count() == 2);
        Assert.Contains(returning, s => s.Booking.Status == BookingStatus.CheckedOut && s.Booking.CheckInOffsetDays + s.Booking.Nights <= 0);
        Assert.Contains(returning, s => s.Booking.Status == BookingStatus.Confirmed && s.Booking.CheckInOffsetDays > 0);

        // A booking that comes from an OTA (its address is a forward), a direct customer who saw the soft opt-in notice and whose booking is
        // confirmed, and a guest who ticked the marketing consent of the online check-in.
        Assert.Contains(stays, s => OtaForwardAddress.IsOtaSource(s.Booking.Source));
        Assert.Contains(
            stays,
            s => s.Booking.Source == BookingSource.Direct && s.Booking.SoftOptInNotice && CrmContactRules.IsPurchase(s.Booking.Status));
        Assert.Contains(stays, s => s.Booking.CheckInMarketingConsent);

        // Two different guests who share a phone: the matching proposes the merge, a person decides. The number is a number for the CRM, and
        // the placeholder every other demo guest has is not, so no other two guests share one.
        var sharing = stays.Where(s => s.Booking.Phone == DemoShowcaseCatalog.CrmSharedPhone).ToList();
        Assert.Equal(2, sharing.Count);
        Assert.Equal(2, sharing.Select(s => s.Booking.Email).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(2, sharing.Select(s => CrmNormalizer.NormalizeName(s.Booking.FirstName, s.Booking.LastName)).Distinct(StringComparer.Ordinal).Count());
        Assert.NotNull(CrmNormalizer.NormalizePhone(DemoShowcaseCatalog.CrmSharedPhone));
        Assert.Null(CrmNormalizer.NormalizePhone("+390000000000"));
    }
}
