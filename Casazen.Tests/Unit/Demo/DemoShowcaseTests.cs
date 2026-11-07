using System.Text.Json;
using Casazen.Core.Condominium;
using Casazen.Core.Demo;
using Casazen.Core.Entities;
using Casazen.Core.Leases;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Enums;
using Casazen.Core.Models;
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
    public void PropertyOwners_FiscalCodeAndIban_PassTheRulesOfTheOwnerRegistry_AndTheAddressIsATestOne()
    {
        // The demo owners are the registry that the owner readiness judges: a fiscal code or an IBAN that fails its check character or checksum
        // would show a fault nobody made. Invented, but valid for the real rules (demo-seed.md).
        foreach (var owner in DemoShowcaseCatalog.PropertyOwners)
        {
            Assert.True(
                OwnerFieldRules.IsValidFiscalId(OwnerFieldRules.NormalizeFiscalCode(owner.FiscalCode), owner.TaxResidenceCountry),
                $"{owner.Key}: the fiscal code fails its check character");
            Assert.True(OwnerFieldRules.IsValidIban(OwnerFieldRules.NormalizeIban(owner.Iban)), $"{owner.Key}: the IBAN fails its checksum");
            Assert.EndsWith("@example.com", owner.Email, StringComparison.Ordinal);
        }
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
            .Concat(DemoShowcaseCatalog.MessageThreads.Select(thread => thread.Scenario))
            .Concat(DemoShowcaseCatalog.HostMessageTemplates.Select(template => template.Scenario))
            .Concat(DemoShowcaseCatalog.FiscalIssuerProfiles.Select(profile => profile.Scenario))
            .Concat(DemoShowcaseCatalog.TouristTaxChannelRules.Select(rule => rule.Scenario))
            .Concat(DemoShowcaseCatalog.WelcomeLinks.Select(link => link.Scenario))
            .Concat(DemoShowcaseCatalog.OnboardingActivations.Select(activation => activation.Scenario))
            .Concat(DemoShowcaseCatalog.RentLedgerComponentExamples.Select(example => example.Scenario))
            .Concat(DemoShowcaseCatalog.CondominiumCoreExamples.Select(example => example.Scenario))
            .Concat(DemoShowcaseCatalog.TenantCondominiumSettlementExamples.Select(example => example.Scenario))
            .Concat(DemoShowcaseCatalog.TenantExpenseProposalExamples.Select(example => example.Scenario))
            .ToHashSet(StringComparer.Ordinal);

        Assert.All(DemoShowcaseCatalog.Scenarios, scenario => Assert.Contains(scenario, covered));
        Assert.Contains(DemoShowcaseCatalog.Properties, p => p.Publish);
        Assert.Contains(DemoShowcaseCatalog.Properties, p => p.Key == DemoShowcaseCatalog.DraftKey && !p.Publish);
        Assert.All(DemoShowcaseCatalog.Properties, p => Assert.Equal(6, p.Place.IstatCode.Length));
    }

    /// <summary>
    /// NF-61, NF-64 (E05/F02/US01/T01): the hub of messages has a demo conversation in each of its three shapes (a direct stay that waits for an
    /// answer, a conversation of Airbnb about a booking, a request of Booking.com with no booking), each about a house and a stay that exist in the
    /// catalog, the direct one on a booking the public checkout makes, the one of a channel on a booking that came from a channel; invented words that
    /// say TEST, the guests of the stays at example.com. The template names no connection to a channel manager: they are rows of the hub only.
    /// </summary>
    [Fact]
    public void MessageHub_TheDemoHasADirectConversation_ABookingOfAChannel_AndARequest_AllSayingTest()
    {
        var threads = DemoShowcaseCatalog.MessageThreads;
        Assert.Contains(DemoShowcaseCatalog.Scenario.MessageHub, DemoShowcaseCatalog.Scenarios);
        Assert.All(threads, t => Assert.Equal(DemoShowcaseCatalog.Scenario.MessageHub, t.Scenario));
        Assert.Equal(threads.Count, threads.Select(t => t.Key).Distinct(StringComparer.Ordinal).Count());

        var direct = Assert.Single(threads, t => t.Kind == DemoMessageThreadKind.Direct);
        var reservation = Assert.Single(threads, t => t.Kind == DemoMessageThreadKind.OtaReservation);
        var request = Assert.Single(threads, t => t.Kind == DemoMessageThreadKind.OtaRequest);
        Assert.Null(request.BookingKey);
        Assert.NotNull(request.Channel);
        Assert.NotNull(reservation.Channel);
        Assert.Null(direct.Channel);

        foreach (var thread in threads)
        {
            var property = Assert.Single(DemoShowcaseCatalog.Properties, p => p.Key == thread.PropertyKey);
            Assert.NotEmpty(thread.Messages);
            Assert.All(thread.Messages, m => Assert.Contains("TEST", m.Text, StringComparison.Ordinal));
            if (thread.Kind != DemoMessageThreadKind.Direct)
                Assert.Contains("TEST", thread.GuestName, StringComparison.Ordinal);

            if (thread.BookingKey is null)
                continue;

            var booking = Assert.Single(property.Bookings, b => b.Key == thread.BookingKey);
            Assert.EndsWith("@example.com", booking.Email, StringComparison.Ordinal);
            if (thread.Kind == DemoMessageThreadKind.Direct)
            {
                // A direct conversation exists only for a booking of the public checkout that was confirmed (GuestMessageRules.StateOf).
                Assert.Equal(BookingSource.Direct, booking.Source);
                Assert.Contains(booking.Status, new[] { BookingStatus.Confirmed, BookingStatus.CheckedIn, BookingStatus.CheckedOut });
                Assert.True(thread.Messages[^1].FromGuest, "the direct conversation of the demo waits for an answer");
            }
            else
            {
                Assert.True(FiscalCopy.IsOtaBookingSource(booking.Source));
            }
        }

        // The messages of every conversation are in the order they were written: the minutes ago fall, and what the host read comes first.
        foreach (var thread in threads)
        {
            var minutes = thread.Messages.Select(m => m.MinutesAgo).ToList();
            Assert.Equal(minutes.OrderByDescending(m => m), minutes);
            var guestMessages = thread.Messages.Where(m => m.FromGuest).ToList();
            Assert.Equal(guestMessages.OrderByDescending(m => m.Read), guestMessages);
        }
    }

    /// <summary>
    /// NF-46: the demo shows a stay taxed guest by guest (a minor exempt by age, an adult the host exempted as resident, one who pays) and a channel
    /// stay in a comune whose demo channel rule makes the channel collect; the rule is for an invented comune only, with an example source.
    /// </summary>
    [Fact]
    public void TouristTax_TheDemoStays_ShowEveryCase_AndTheChannelRuleTouchesNoRealComune()
    {
        var bookings = DemoShowcaseCatalog.Properties.SelectMany(p => p.Bookings.Select(b => (Property: p, Booking: b))).ToList();

        var perGuest = Assert.Single(bookings, b => b.Booking.Scenario == DemoShowcaseCatalog.Scenario.TouristTaxPerGuest);
        var guests = perGuest.Booking.Guests!;
        Assert.Contains(guests, g => g.AgeAtCheckIn < 14 && g.TaxExemptionCode is null);
        Assert.Contains(guests, g => g.AgeAtCheckIn >= 18 && g.TaxExemptionCode == TouristTaxExemptionReason.ResidentCode);
        Assert.Contains(guests, g => g.AgeAtCheckIn >= 18 && g.TaxExemptionCode is null);
        Assert.Empty(AlloggiatiRecordRules.CompositionErrors(guests.Select(g => g.Type).ToList()));

        var channelStay = Assert.Single(bookings, b => b.Booking.Scenario == DemoShowcaseCatalog.Scenario.TouristTaxChannelCollected);
        var rule = Assert.Single(DemoShowcaseCatalog.TouristTaxChannelRules);
        Assert.Equal(channelStay.Property.Key, rule.PropertyKey);
        Assert.Equal(channelStay.Booking.Source, rule.Channel);
        Assert.Equal(TouristTaxCollector.Channel, rule.Collector);
        Assert.StartsWith("https://example.com/", rule.SourceUrl, StringComparison.Ordinal);
        Assert.Contains("TEST", rule.Notes, StringComparison.Ordinal);
        Assert.StartsWith("999", channelStay.Property.Place.IstatCode, StringComparison.Ordinal);
        Assert.NotNull(channelStay.Property.DeclaredTouristTaxRate);
        Assert.Contains("TEST", channelStay.Property.DeclaredTouristTaxRate!.SourceNote, StringComparison.Ordinal);
        Assert.All(bookings.Where(b => b.Booking.Guests is not null), b =>
            Assert.EndsWith("@example.com", b.Booking.Email, StringComparison.Ordinal));
    }

    /// <summary>
    /// E03/F04/US02/T04: the demo has a stay whose identity verification passed online: the agent's Accept, the consent with a test version, the
    /// verified guest is the one who leaves the document (the head of group; the member skips the step), the lock code went to the check-in page
    /// and by e-mail. Invented guests, @example.com addresses.
    /// </summary>
    [Fact]
    public void IdentityVerification_TheDemoHasAStayWhoseVerificationPassed()
    {
        var template = Assert.Single(DemoShowcaseCatalog.CheckIns, c => c.Scenario == DemoShowcaseCatalog.Scenario.CheckInIdentityVerified);
        var property = Assert.Single(DemoShowcaseCatalog.Properties, p => p.Key == template.PropertyKey);
        var booking = Assert.Single(property.Bookings, b => b.Key == template.BookingKey);

        Assert.Equal(BiometricOutcome.Accept, template.BiometricOutcome);
        Assert.Equal(VerificationMethod.Biometric, template.Method);
        Assert.True(template.HasConsent);
        Assert.False(template.OperatorVerified);
        Assert.Contains("TEST", template.ConsentVersion, StringComparison.Ordinal);
        Assert.Equal([AccessCodeReleaseChannels.CheckInScreen, AccessCodeReleaseChannels.Email], template.LockCodeChannels);

        Assert.NotNull(booking.Guests);
        var verified = booking.Guests![template.VerifiedGuestPosition];
        Assert.True(AlloggiatiRecordRules.RequiresDocument(verified.Type));
        Assert.True(verified.AgeAtCheckIn >= 18);
        Assert.All(booking.Guests.Where((_, position) => position != template.VerifiedGuestPosition), g => Assert.False(AlloggiatiRecordRules.RequiresDocument(g.Type)));
        Assert.EndsWith("@example.com", booking.Email, StringComparison.Ordinal);
    }

    /// <summary>
    /// NF-40, NF-45 (E03/F01/US01/T04): the check-in wizard has a demo stay in each of the four states (expected, arrived, departed, no-show), all
    /// with registered guests of a valid composition, and one stay with data to complete (a birth place missing) and a document expired before
    /// the arrival. Invented guests, @example.com addresses.
    /// </summary>
    [Fact]
    public void CheckInWizard_TheDemoHasStaysInTheFourStates_AndOneWithDataToComplete()
    {
        var bookings = DemoShowcaseCatalog.Properties.SelectMany(p => p.Bookings).ToList();
        DemoBookingTemplate Of(string scenario) => Assert.Single(bookings, b => b.Scenario == scenario);

        var expected = Of(DemoShowcaseCatalog.Scenario.CheckInWizardExpected);
        var arrived = Of(DemoShowcaseCatalog.Scenario.CheckInWizardArrived);
        var departed = Of(DemoShowcaseCatalog.Scenario.CheckInWizardDeparted);
        var noShow = Of(DemoShowcaseCatalog.Scenario.CheckInWizardNoShow);
        var incomplete = Of(DemoShowcaseCatalog.Scenario.CheckInWizardIncomplete);

        Assert.True(expected is { Status: BookingStatus.Confirmed, NoShow: false } && expected.CheckInOffsetDays > 0);
        Assert.True(arrived.Status == BookingStatus.CheckedIn && arrived.CheckInOffsetDays <= 0);
        Assert.Equal(BookingStatus.CheckedOut, departed.Status);
        // A no-show is a confirmed stay whose check-in day has come (the rule of the no-show).
        Assert.True(noShow is { Status: BookingStatus.Confirmed, NoShow: true } && noShow.CheckInOffsetDays < 0);
        Assert.All(bookings.Where(b => b.NoShow), b => Assert.Equal(BookingStatus.Confirmed, b.Status));

        foreach (var stay in new[] { expected, arrived, departed, noShow, incomplete })
        {
            Assert.NotNull(stay.Guests);
            Assert.NotEmpty(stay.Guests!);
            Assert.Empty(AlloggiatiRecordRules.CompositionErrors(stay.Guests!.Select(g => g.Type).ToList()));
            Assert.EndsWith("@example.com", stay.Email, StringComparison.Ordinal);
        }

        Assert.Contains(incomplete.Guests!, g => g.BirthPlaceMissing);
        Assert.Contains(incomplete.Guests!, g => g.DocumentExpired && AlloggiatiRecordRules.RequiresDocument(g.Type));
        Assert.All(new[] { expected, arrived, departed, noShow }, stay =>
            Assert.DoesNotContain(stay.Guests!, g => g.BirthPlaceMissing || g.DocumentExpired));
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
    public void WelcomeLinks_TheDemoLink_IsAFixedTestTokenOfAShowcaseHouse_WithOneLinkForEachHouse()
    {
        var links = DemoShowcaseCatalog.WelcomeLinks;

        Assert.Contains(DemoShowcaseCatalog.Scenario.CrmWelcomeLink, DemoShowcaseCatalog.Scenarios);
        Assert.NotEmpty(links);
        Assert.Equal(links.Count, links.Select(link => link.Key).Distinct(StringComparer.Ordinal).Count());
        // The token is unique in the whole table, and a house has one active link.
        Assert.Equal(links.Count, links.Select(link => link.Token).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(links.Count, links.Select(link => link.PropertyKey).Distinct(StringComparer.Ordinal).Count());
        Assert.All(links, link =>
        {
            Assert.Contains(DemoShowcaseCatalog.Properties, property => property.Key == link.PropertyKey);
            Assert.Contains("TEST", link.Token, StringComparison.Ordinal);
            Assert.InRange(link.Token.Length, 1, PropertyWelcomeLink.TokenMaxLength);
            Assert.Matches("^[A-Za-z0-9_-]+$", link.Token);
        });
        Assert.Contains(DemoShowcaseCatalog.CrmWelcomeToken, links.Select(link => link.Token));
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
    public void DocumentArchive_TheDemoHouseHasTheCertificatesOfItsRegion_AndAnInventedReceiptOfItsTouristTaxPayment()
    {
        Assert.Contains(DemoShowcaseCatalog.Scenario.DocumentArchive, DemoShowcaseCatalog.Scenarios);
        var house = Assert.Single(DemoShowcaseCatalog.Properties, p => p.Scenarios.Contains(DemoShowcaseCatalog.Scenario.DocumentArchive));

        // NF-99: the archive is a read model over the tables the showcase already fills. A published house gets the TEST certificates of its
        // region from the compliance fill (real PDF bytes, marked as showcase data), so the host finds property documents under Documents...
        Assert.True(house.Publish);
        Assert.NotEmpty(DemoRequiredDocuments.ForRegion(new ComplianceOptions().RequiredDocuments, house.Place.RegionCode));

        // ...and the receipt of the tourist tax payment of that very house, an invented one-page PDF that says it is test data.
        var payment = Assert.Single(DemoShowcaseCatalog.TouristTaxRemittances, t => t.PropertyKey == house.Key);
        Assert.Equal(DemoShowcaseCatalog.Scenario.TouristTaxPaid, payment.Scenario);
        Assert.Contains(DemoShowcaseCatalog.Scenario.TouristTaxPaid, house.Scenarios);
        Assert.Contains("TEST", payment.Reference, StringComparison.Ordinal);
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

    /// <summary>
    /// NF-74, NF-77 (E08/F03/US03/T02): the booking page of the demo shows a booking of a channel with all its amounts and flags, one with only what
    /// Octorate gives today, a booking of the booking site whose mirror is synced and a booking the host entered whose change Octorate refused. The
    /// reservations, the amounts and the mirrors are invented (TEST) and come only with the bookings that can have them.
    /// </summary>
    [Fact]
    public void BookingOtaAmounts_TheDemoHasTwoBookingsOfAChannelASyncedMirrorAndAConflict_AllInvented()
    {
        var stays = DemoShowcaseCatalog.Properties
            .SelectMany(p => p.Bookings.Where(b => b.Scenario == DemoShowcaseCatalog.Scenario.BookingOtaAmounts))
            .ToList();

        Assert.Contains(DemoShowcaseCatalog.Scenario.BookingOtaAmounts, DemoShowcaseCatalog.Scenarios);
        Assert.NotEmpty(stays);
        Assert.Equal(stays.Count, stays.Select(s => s.Key).Distinct(StringComparer.Ordinal).Count());
        Assert.All(stays, s => Assert.EndsWith("@example.com", s.Email, StringComparison.Ordinal));
        Assert.All(stays, s => Assert.NotNull(s.Ota));
        Assert.All(stays, s => Assert.Equal(BookingStatus.Confirmed, s.Status));

        // The reservation ids say TEST, fit the column and are unique (the index of the table is on the org and the id).
        var reservationIds = stays.Select(s => s.Ota!.ReservationId).ToList();
        Assert.Equal(reservationIds.Count, reservationIds.Distinct(StringComparer.Ordinal).Count());
        Assert.All(reservationIds, id =>
        {
            Assert.Contains("TEST", id, StringComparison.Ordinal);
            Assert.InRange(id.Length, 1, Booking.OctorateReservationIdMaxLength);
        });

        // Two bookings of a channel: all the amounts and the flags, and only the amount paid (the rest is «not available»).
        var channel = stays.Where(s => OtaChannelActionPolicy.IsChannelBooking(s.Source, s.Ota!.ReservationId)).ToList();
        Assert.Equal(2, channel.Count);
        var full = Assert.Single(channel, s => s.Ota!.Commission is not null).Ota!;
        Assert.NotNull(full.Net);
        Assert.NotNull(full.Paid);
        Assert.NotNull(full.Balance);
        Assert.NotNull(full.CompanyCollect);
        Assert.NotNull(full.CommissionIncludedInPayments);
        // What the host receives is what the guest paid less the channel's commission, and nothing is left to pay.
        Assert.Equal(full.Paid - full.Commission, full.Net);
        Assert.Equal(0m, full.Balance);
        var sparse = Assert.Single(channel, s => s.Ota!.Commission is null).Ota!;
        Assert.NotNull(sparse.Paid);
        Assert.Null(sparse.Net);
        Assert.Null(sparse.Balance);
        Assert.Null(sparse.CompanyCollect);

        // Every figure is to the cent, and a booking that is not of a channel has neither a figure nor a flag: its money is CasaZen's.
        foreach (var figure in stays.SelectMany(s => new[] { s.Ota!.Commission, s.Ota.Net, s.Ota.Paid, s.Ota.Balance }).OfType<decimal>())
            Assert.Equal(figure, decimal.Round(figure, 2));

        Assert.All(stays.Except(channel), s =>
        {
            Assert.Null(s.Ota!.Commission);
            Assert.Null(s.Ota.Net);
            Assert.Null(s.Ota.Paid);
            Assert.Null(s.Ota.Balance);
            Assert.Null(s.Ota.CompanyCollect);
            Assert.Null(s.Ota.CommissionIncludedInPayments);
        });

        // A booking of the booking site that CasaZen sent to Octorate: it has a reservation there and is not a booking of a channel.
        var synced = Assert.Single(stays, s => s.Ota!.Sync?.State == OctorateSyncState.Synced);
        Assert.Equal(BookingSource.Direct, synced.Source);
        Assert.False(OtaChannelActionPolicy.IsChannelBooking(synced.Source, synced.Ota!.ReservationId));
        Assert.Null(synced.Ota.Sync!.ErrorCode);

        // A booking the host entered, whose change Octorate refused: a code CasaZen knows and the stay Octorate holds, which is not the booking's.
        var conflict = Assert.Single(stays, s => s.Ota!.Sync?.State == OctorateSyncState.Conflict);
        var mirror = conflict.Ota!.Sync!;
        Assert.Equal(BookingSource.Manual, conflict.Source);
        Assert.Contains(mirror.ErrorCode!, OctorateSyncErrorCodes.MessageKeys.Keys);
        Assert.NotEqual(0, mirror.VersionCheckInShiftDays);
        Assert.NotNull(mirror.VersionGuests);
        Assert.True(OctorateBookingSync.IsRetryable(mirror.State));

        // The channel bookings are mirrored by nobody: the import leaves no row for them.
        Assert.All(channel, s => Assert.Null(s.Ota!.Sync));
    }

    /// <summary>
    /// NF-52 (E03/F02/US01/T03): the compliance traffic light has a stay in yellow (the guests entrusted to the channel manager, at an Italian house whose
    /// stored sender is the channel manager) and one in green (the receipt of the arrival day archived, inside the days the light looks at). Both have
    /// registered guests of a valid composition, invented and at example.com; the receipt is stored under a key valid in the private bucket.
    /// </summary>
    [Fact]
    public void ComplianceTrafficLight_TheDemoHasAnEntrustedStayInYellowAndOneWithAnArchivedReceiptInGreen_AllInvented()
    {
        var stays = DemoShowcaseCatalog.Properties
            .SelectMany(p => p.Bookings.Where(b => b.Compliance is not null).Select(b => (Property: p, Booking: b)))
            .ToList();

        Assert.Contains(DemoShowcaseCatalog.Scenario.ComplianceTrafficLight, DemoShowcaseCatalog.Scenarios);
        Assert.Equal(2, stays.Count);
        Assert.All(stays, s => Assert.Equal(DemoShowcaseCatalog.Scenario.ComplianceTrafficLight, s.Booking.Scenario));
        Assert.All(stays, s => Assert.EndsWith("@example.com", s.Booking.Email, StringComparison.Ordinal));
        Assert.All(stays, s =>
        {
            Assert.NotEmpty(s.Booking.Guests!);
            Assert.Empty(AlloggiatiRecordRules.CompositionErrors(s.Booking.Guests!.Select(g => g.Type).ToList()));
        });

        // Yellow: the guests are entrusted, and the house they are at is sent by the channel manager (the only one in the catalog), in Italy: a house
        // outside Italy has no police sender, and the invented comune of the tax rule has no real code. The stay has arrived and is not a short one.
        var entrusted = Assert.Single(stays, s => s.Booking.Compliance!.GuestsEntrusted);
        var sender = Assert.Single(DemoShowcaseCatalog.Properties, p => p.ComplianceSender is not null);
        Assert.Equal(entrusted.Property.Key, sender.Key);
        Assert.Equal(ComplianceSenderType.ChannelManager, sender.ComplianceSender!.Sender);
        Assert.True(sender.ComplianceSender.ActivationDeclared);
        Assert.NotEqual("TIC", sender.Place.RegionCode);
        Assert.DoesNotMatch("^999", sender.Place.IstatCode);
        Assert.Equal(BookingStatus.CheckedIn, entrusted.Booking.Status);
        Assert.Equal(0, entrusted.Booking.CheckInOffsetDays);
        Assert.True(entrusted.Booking.Nights > 1);
        Assert.False(entrusted.Booking.Compliance!.ReceiptArchived);

        // Green: a finished stay with the receipt archived, whose arrival day is inside the days the status engine looks at.
        var archived = Assert.Single(stays, s => s.Booking.Compliance!.ReceiptArchived);
        Assert.False(archived.Booking.Compliance!.GuestsEntrusted);
        Assert.Equal(BookingStatus.CheckedOut, archived.Booking.Status);
        Assert.InRange(archived.Booking.CheckInOffsetDays, 1 - new ComplianceOptions().StatusLookbackDays, -1);
        Assert.Null(archived.Property.ComplianceSender);

        // The receipt is stored under a key the private bucket accepts, named by the server and not by the host.
        var key = StorageKeys.ManualComplianceProof(Guid.NewGuid(), Guid.NewGuid(), "ricevuta-di-test.pdf");
        Assert.True(StorageKeys.IsValid(key), key);
        Assert.StartsWith("compliance-proofs/", key, StringComparison.Ordinal);
        Assert.InRange(key.Length, 1, ManualComplianceProof.FileKeyMaxLength);
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

    [Fact]
    public void InvoiceData_TheDemoHasAFinishedDirectStayWithAnInvoiceRecipientThatPassesTheRulesOfARealOne_AllInvented()
    {
        var stays = DemoShowcaseCatalog.Properties
            .SelectMany(p => p.Bookings.Where(b => b.Scenario == DemoShowcaseCatalog.Scenario.InvoiceData))
            .ToList();

        // NF-81: the booking of the host has an invoice recipient to show, so the scenario has template data and a scenario without it fails.
        Assert.Contains(DemoShowcaseCatalog.Scenario.InvoiceData, DemoShowcaseCatalog.Scenarios);
        var stay = Assert.Single(stays);
        var data = stay.InvoiceData;
        Assert.NotNull(data);
        Assert.Equal(BookingSource.Direct, stay.Source);
        Assert.Equal(BookingStatus.CheckedOut, stay.Status);
        Assert.EndsWith("@example.com", stay.Email, StringComparison.Ordinal);

        // Invented and obviously so: TEST in the name, an address at example.com (the PEC is at a subdomain of it, pec.example.com).
        Assert.Contains("TEST", data.Name, StringComparison.Ordinal);
        Assert.EndsWith(".example.com", data.Pec!, StringComparison.Ordinal);
        Assert.Equal(InvoiceDataSource.Host, data.Source);

        // Valid for the rules a recipient saved through the service obeys: no failure, and the recipient is identified.
        var state = new SaveInvoiceDataCommand
        {
            RecipientKind = data.Kind,
            Name = data.Name,
            FiscalCode = data.FiscalCode,
            VatNumber = data.VatNumber,
            AddressStreet = data.AddressStreet,
            AddressCivic = data.AddressCivic,
            PostalCode = data.PostalCode,
            City = data.City,
            Province = data.Province,
            CountryCode = BookingInvoiceData.DefaultCountryCode,
            SdiRecipientCode = data.SdiRecipientCode,
            Pec = data.Pec,
        };
        Assert.Empty(InvoiceDataFormats.Validate(state, requireIdentification: true));
    }

    /// <summary>
    /// E10/F01/US01/T04 (NF-104, NF-106): the activation with Octorate has template data (a scenario without it fails here and in the coverage test
    /// above): a test connection whose account says TEST, three short-rent houses of the template whose bookings flow is, by the real rules of the
    /// flow status, in three different states (active; every prerequisite met and not activated; listing not confirmed), invented listing ids that
    /// say TEST, and one deferred part that the plan lets the host defer.
    /// </summary>
    [Fact]
    public void OnboardingActivation_TheDemoOrgHasATestConnection_ThreeHousesInDifferentFlowStates_AndADeferredPart()
    {
        Assert.Contains(DemoShowcaseCatalog.Scenario.OnboardingActivation, DemoShowcaseCatalog.Scenarios);
        var activation = Assert.Single(DemoShowcaseCatalog.OnboardingActivations);
        Assert.Equal(DemoShowcaseCatalog.Scenario.OnboardingActivation, activation.Scenario);
        Assert.Contains("TEST", activation.AccountId, StringComparison.Ordinal);
        Assert.True(activation.AccountId.Length <= OctorateConnection.AccountIdMaxLength);
        Assert.Equal(3, activation.Properties.Select(p => p.PropertyKey).Distinct(StringComparer.Ordinal).Count());

        var connected = FlowPrerequisite.Holds(OnboardingPrerequisiteKeys.Connection);
        var states = new List<(string State, string? Reason)>();
        foreach (var house in activation.Properties)
        {
            var template = Assert.Single(DemoShowcaseCatalog.Properties, p => p.Key == house.PropertyKey);
            Assert.Equal(RentalMode.Short, template.Mode);
            Assert.Contains(DemoShowcaseCatalog.Scenario.OnboardingActivation, template.Scenarios);
            if (house.AccommodationId is { } accommodation)
            {
                Assert.Contains("TEST", accommodation, StringComparison.Ordinal);
                Assert.True(accommodation.Length <= OctorateListingMapping.AccommodationIdMaxLength);
            }

            // The bookings flow needs the connection and the confirmed listing (T02): the state the house shows after a reset.
            var mapping = house.AccommodationId is null
                ? FlowPrerequisite.Lacks(OnboardingPrerequisiteKeys.Mapping, OnboardingFlowReasons.MappingUnconfirmed)
                : FlowPrerequisite.Holds(OnboardingPrerequisiteKeys.Mapping);
            var row = house.ActivatedFlows.Contains(OnboardingFlow.Bookings)
                ? new PropertyFlowActivation { PropertyId = Guid.NewGuid(), Flow = OnboardingFlow.Bookings, ActivatedByUserId = "auth0|demo" }
                : null;
            var status = OnboardingFlowStatusRules.Evaluate(
                new FlowPropertyFacts(Guid.NewGuid(), template.Name, template.Place.City, template.Mode, true),
                OnboardingFlow.Bookings,
                row,
                FlowReadiness.Needing(connected, mapping));
            states.Add((status.State, status.Reason));
        }

        Assert.Equal(3, states.Distinct().Count());
        Assert.Contains((OnboardingFlowStates.Active, (string?)null), states);
        Assert.Contains((OnboardingFlowStates.ToComplete, (string?)OnboardingFlowReasons.FlowNotActivated), states);
        Assert.Contains((OnboardingFlowStates.ToComplete, (string?)OnboardingFlowReasons.MappingUnconfirmed), states);

        // The deferred part is one the plan lets the host defer, with a reminder in the future.
        Assert.True(OnboardingPartCatalog.IsDeferrable(activation.DeferredPart));
        Assert.InRange(activation.RemindInDays, 1, OnboardingPlanLimits.MaxRemindDays);
    }

    /// <summary>
    /// NF-36 (E08/F05/US01/T02): the scenario has template data (a published house with day rules), every rule is one the service would store (the
    /// same function decides), the nights are ahead of the demo stays so a rule never sits on a booking, and the showcase shows each kind of rule.
    /// </summary>
    [Fact]
    public void CalendarDayRules_TheDemoHouseHasRulesTheServiceWouldStore_OnNightsWithoutAStay()
    {
        Assert.Contains(DemoShowcaseCatalog.Scenario.CalendarDayRules, DemoShowcaseCatalog.Scenarios);
        var house = Assert.Single(
            DemoShowcaseCatalog.Properties,
            p => p.Scenarios.Contains(DemoShowcaseCatalog.Scenario.CalendarDayRules));
        var rules = house.DayRules ?? [];

        Assert.True(house.Publish);
        Assert.NotEmpty(rules);
        Assert.Equal(rules.Count, rules.Select(rule => rule.OffsetDays).Distinct().Count());
        foreach (var rule in rules)
        {
            var values = new DayRuleValues(
                rule.Price, rule.MinNights, rule.MaxNights, rule.ClosedToArrival, rule.ClosedToDeparture, rule.MinAdvanceDays, rule.MaxAdvanceDays);
            Assert.True(values.HasAny, $"The rule of night +{rule.OffsetDays} has no value: a row exists only while a value is set.");
            Assert.Null(DayRuleRules.Validate(values));
            Assert.True(rule.Price is not { } price || price == DayRuleRules.RoundPrice(price));
            Assert.True(rule.OffsetDays > 0, "A rule of the demo is for a night ahead of the reset.");
        }

        // The nights of the stays of the house carry no rule.
        var stayNights = house.Bookings
            .SelectMany(booking => Enumerable.Range(booking.CheckInOffsetDays, booking.Nights))
            .ToHashSet();
        Assert.DoesNotContain(rules, rule => stayNights.Contains(rule.OffsetDays));

        // Each kind of rule is on show: a price of its own, a longer minimum stay, a closed arrival, a closed departure, a maximum stay and an advance.
        Assert.Contains(rules, rule => rule.Price is not null);
        Assert.Contains(rules, rule => rule.MinNights is > 1);
        Assert.Contains(rules, rule => rule.ClosedToArrival);
        Assert.Contains(rules, rule => rule.ClosedToDeparture);
        Assert.Contains(rules, rule => rule.MaxNights is not null);
        Assert.Contains(rules, rule => rule.MinAdvanceDays is not null || rule.MaxAdvanceDays is not null);
    }

    [Fact]
    public void FiscalIssuerProfile_TheDemoOrgHasACompleteProfileOfItsIssuer_InventedAndNeedingNeitherTheFlagNorAConnection()
    {
        // FIS-02, NF-82: a scenario without template data fails (the coverage test above), and the profile is complete and obviously test data.
        Assert.Contains(DemoShowcaseCatalog.Scenario.FiscalIssuerProfile, DemoShowcaseCatalog.Scenarios);
        var profile = Assert.Single(DemoShowcaseCatalog.FiscalIssuerProfiles);
        Assert.Equal(DemoShowcaseCatalog.Scenario.FiscalIssuerProfile, profile.Scenario);
        Assert.Contains("TEST", profile.LegalName, StringComparison.Ordinal);
        Assert.True(profile.ConfirmedOffsetDays <= 0);

        // The accountant is the one mode that needs no flag and no Octorate connection, so the demo works in every environment.
        Assert.Equal(DocumentIssuerMode.Accountant, profile.Mode);
        Assert.True(Enum.IsDefined(profile.Regime));
        var issuer = new IssuerData(
            profile.LegalName, profile.AddressStreet, profile.AddressCivic, profile.PostalCode, profile.City, profile.Province, "IT",
            profile.FatturaPaRegimeCode, null, null, null);

        // Complete for the mode CasaZen too, once the organization has its VAT number on its tax profile: nothing of the profile itself is missing.
        Assert.Empty(FiscalDocumentRules.MissingIssuerFields(issuer, profile.Regime, profile.FatturaPaRegimeCode, "12345678903", null));
        Assert.Equal(5, profile.PostalCode.Length);
        Assert.Equal(2, profile.Province.Length);
    }

    [Fact]
    public void RentLedgerComponents_FixedAdvance_TwelveMonthsOfRentPlusAdvance()
    {
        Assert.Contains(DemoShowcaseCatalog.Scenario.RentLedgerComponents, DemoShowcaseCatalog.Scenarios);
        var example = Assert.Single(DemoShowcaseCatalog.RentLedgerComponentExamples);
        Assert.Equal(DemoShowcaseCatalog.Scenario.RentLedgerComponents, example.Scenario);
        Assert.Equal(12, example.Months);

        var components = RentLedgerLine.FixedContract(example.Rent, example.CondominiumAdvance);
        Assert.Equal(1_050m, RentLedgerLine.Sum(components));
    }

    [Fact]
    public void CondominiumCore_CasaA_SettlesTheYearWithoutMixingExtraordinaryIntoOrdinary()
    {
        Assert.Contains(DemoShowcaseCatalog.Scenario.CondominiumCore, DemoShowcaseCatalog.Scenarios);
        var example = Assert.Single(DemoShowcaseCatalog.CondominiumCoreExamples);
        Assert.Equal(DemoShowcaseCatalog.Scenario.CondominiumCore, example.Scenario);
        Assert.Equal(6, example.FiscalStartMonth);
        var year = FiscalYear.ForStartYear(2024, example.FiscalStartMonth);
        Assert.Equal(new DateOnly(2024, 6, 1), year.Start);
        Assert.Equal(new DateOnly(2025, 5, 31), year.End);

        var settlement = Conguaglio.Compute(example.FinalAmount, example.Extraordinary, example.Prior, example.Paid);
        Assert.NotNull(settlement);
        Assert.Equal(example.ExpectedSettlement, settlement.Amount);
        Assert.Equal(Conguaglio.Direction.Debit, settlement.Direction);
        Assert.Equal(0m, example.Extraordinary);
    }

    [Fact]
    public void TenantSettlement_RecoverableMinusPostedAdvances_IsTheDebit()
    {
        Assert.Contains(DemoShowcaseCatalog.Scenario.TenantCondominiumSettlement, DemoShowcaseCatalog.Scenarios);
        var example = Assert.Single(DemoShowcaseCatalog.TenantCondominiumSettlementExamples);
        var totals = TenantSettlementMath.Settle(example.RecoverableFinal, example.PriorBalance, example.AdvancesPosted);
        Assert.Equal(example.ExpectedSettlement, totals.Amount);
        Assert.Equal(TenantSettlementDirection.Debit, totals.Direction);
    }

    [Fact]
    public void TenantExpenseProposal_WithoutAYear_SplitsThePreventivoAndKeepsTheConsuntivo()
    {
        Assert.Contains(DemoShowcaseCatalog.Scenario.TenantExpenseProposal, DemoShowcaseCatalog.Scenarios);
        var example = Assert.Single(DemoShowcaseCatalog.TenantExpenseProposalExamples);
        var proposal = TenantExpenseProposal.Resolve(
            false,
            null,
            null,
            null,
            new TenantExpenseProposal.Request(null, null, RentCadence.Monthly, example.PreviousConsuntivo, example.Preventivo, null));
        Assert.Equal(TenantExpenseProposal.Source.Statements, proposal.Source);
        Assert.Equal(example.ExpectedInstallment, proposal.InstallmentAmount);
        Assert.Equal(example.Installments, proposal.Installments);
        Assert.Equal(example.Preventivo, proposal.AnnualAmount);
        Assert.True(proposal.SettledAtConsuntivo);
        Assert.Equal(example.PreviousConsuntivo, proposal.PreviousConsuntivo);
    }

    [Fact]
    public void LandlordBankLink_HasTemplateData_ThatIsNotABankNumber()
    {
        Assert.Contains(DemoShowcaseCatalog.Scenario.LandlordBankLink, DemoShowcaseCatalog.Scenarios);
        var house = Assert.Single(
            DemoShowcaseCatalog.Properties,
            p => p.Scenarios.Contains(DemoShowcaseCatalog.Scenario.LandlordBankLink));
        Assert.Equal(RentalMode.Long, house.Mode);
        Assert.Contains("TEST", DemoShowcaseCatalog.BankLinkAccountId, StringComparison.Ordinal);
        Assert.StartsWith("fca_", DemoShowcaseCatalog.BankLinkAccountId, StringComparison.Ordinal);
        Assert.DoesNotContain("IT", DemoShowcaseCatalog.BankLinkAccountId, StringComparison.Ordinal);
    }
}
