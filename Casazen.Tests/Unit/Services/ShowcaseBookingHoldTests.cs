using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Options;
using Casazen.Core.Services;
using Casazen.Core.Suppliers;
using Casazen.Infrastructure.Email.Templates;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// SP-10: the first half of the booking from a supplier's public showcase — the hold. A free slot is held for 30 minutes under the
/// supplier's calendar lock, judged by the real slot planner (which counts the holds) with no cache; the customer gets one e-mail
/// with the link that checks the address and the supplier hears nothing; the same client request id is the same hold; an address
/// can have three bookings waiting. The lock itself, and N customers booking one slot at once, need PostgreSQL
/// (<c>ShowcaseBookingConcurrencyPostgresTests</c>); here the same rules run one after the other on the real planner.
/// </summary>
public class ShowcaseBookingHoldTests
{
    private static readonly TimeSpan HoldMinutes = TimeSpan.FromMinutes(30);

    [Fact]
    public async Task CreateHold_AFreeSlot_IsHeldForThirtyMinutes_WithTheDataOfTheCustomerInItsPayload()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var supplier = await s.EnableBookingAsync();

        var hold = await s.Kit.Booking.CreateHoldAsync(supplier, await s.InputAsync());

        var saved = Assert.Single(await s.HoldsAsync());
        Assert.Equal(hold.Id, saved.Id);
        Assert.Equal(s.SupplierOrgId, saved.OrgId);
        Assert.Equal(ServiceRequestScenario.FridayAt10, saved.StartUtc);
        Assert.Equal(ServiceRequestScenario.FridayAt10.AddMinutes(ServiceRequestScenario.ServiceMinutes), saved.EndUtc);
        Assert.Equal(s.Clock.GetUtcNow().UtcDateTime + HoldMinutes, saved.ExpiresAt);
        Assert.Equal(saved.ExpiresAt, hold.ExpiresAt);
        Assert.Null(saved.ConsumedAt);
        Assert.Null(saved.ServiceRequestId);
        Assert.Matches("^[0-9A-Z]{10}$", saved.PublicCode);
        Assert.Equal(s.Kit.CustomerIndex.HashEmail(ShowcaseScenario.CustomerEmail), saved.EmailHash);

        var payload = ShowcaseBookingPayload.FromJson(saved.Payload)!;
        Assert.Equal(ShowcaseScenario.CustomerName, payload.FullName);
        Assert.Equal(ShowcaseScenario.CustomerEmail, payload.Email);
        Assert.Equal("+393331234567", payload.Phone);
        Assert.Equal(ShowcaseScenario.City, payload.City);
        Assert.Equal(ShowcaseScenario.Address, payload.Address);
        Assert.Equal(ServiceRequestScenario.ServiceName, payload.ServiceName);
        Assert.Equal(ServiceCategories.Cleaning, payload.Category);
        Assert.Equal(ServiceRequestScenario.ServicePriceCents, payload.EstimatedAmountCents);
        Assert.Equal(ServiceRequestTestKit.PrivacyNoticeVersion, payload.PrivacyNoticeVersion);
        Assert.Equal(s.Clock.GetUtcNow().UtcDateTime, payload.PrivacyAcceptedAt);
        Assert.Equal(ShowcaseScenario.ConsentIp, payload.ConsentIp);
    }

    [Fact]
    public async Task CreateHold_NothingReachesTheSupplier_NoRequestExists_NoCustomerYet()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var supplier = await s.EnableBookingAsync();
        s.ForgetNotifications();

        await s.Kit.Booking.CreateHoldAsync(supplier, await s.InputAsync());

        Assert.Empty(await s.Db.ServiceRequests.IgnoreQueryFilters().Where(r => r.RentalContext == ServiceRequestRentalContext.Showcase).ToListAsync());
        Assert.Empty(await s.CustomersAsync());
        Assert.DoesNotContain(s.Emails.Snapshot(), e => e.To == "supplier@test.com");
        Assert.Empty(s.Pushes);
    }

    [Fact]
    public async Task CreateHold_QueuesOneEmailToTheCustomerWithTheLinkThatChecksTheAddress()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var supplier = await s.EnableBookingAsync();
        s.ForgetNotifications();

        var hold = await s.Kit.Booking.CreateHoldAsync(supplier, await s.InputAsync());

        var email = Assert.Single(s.Emails.Snapshot());
        Assert.Equal(ShowcaseScenario.CustomerEmail, email.To);
        Assert.Equal(EmailTemplates.Names.SupplierBookingVerification, email.Template);
        Assert.Contains("Supplier Srl", email.Content.Subject);
        Assert.Contains(ShowcaseScenario.CustomerName, email.Content.HtmlBody);
        Assert.Contains($"/fornitori/{ShowcaseScenario.Slug}/conferma?hold={hold.Id:D}&amp;token=", email.Content.HtmlBody);
        Assert.Contains("30 minuti", email.Content.HtmlBody);
        var token = s.VerificationTokenOf(hold.Id);
        Assert.Equal(43, token.Length);
        // Only the hash of the token is stored.
        var saved = Assert.Single(await s.HoldsAsync());
        Assert.Equal(ShowcaseBookingTokens.Hash(token), saved.TokenHash);
        Assert.DoesNotContain(token, saved.Payload ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CreateHold_TheEmailIsInTheLanguageOfTheCustomer()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var supplier = await s.EnableBookingAsync();
        s.ForgetNotifications();

        await s.Kit.Booking.CreateHoldAsync(supplier, await s.InputAsync(change: i => i with { Locale = "en" }));

        var email = Assert.Single(s.Emails.Snapshot());
        Assert.Contains("Confirm your request to Supplier Srl", email.Content.Subject);
        Assert.Contains("lang=\"en\"", email.Content.HtmlBody);
        Assert.Contains("We keep the time free for 30 minutes", email.Content.HtmlBody);
    }

    [Fact]
    public async Task CreateHold_TheHeldSlot_IsNotOfferedAnymore_UntilTheHoldExpires()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var supplier = await s.EnableBookingAsync();
        var before = await s.FreeSlotsOfFridayAsync();
        Assert.Contains(ServiceRequestScenario.FridayAt10, before);

        await s.Kit.Booking.CreateHoldAsync(supplier, await s.InputAsync());

        var held = await s.FreeSlotsOfFridayAsync();
        // The slot itself and the ones that touch it with the buffer of 30 minutes between two jobs.
        Assert.DoesNotContain(ServiceRequestScenario.FridayAt10, held);
        Assert.DoesNotContain(ServiceRequestScenario.FridayAt10.AddHours(-1), held);
        Assert.DoesNotContain(ServiceRequestScenario.FridayAt10.AddHours(1), held);
        Assert.Contains(ServiceRequestScenario.FridayAt14, held);

        s.Clock.Advance(HoldMinutes + TimeSpan.FromSeconds(1));
        Assert.Equal(before, await s.FreeSlotsOfFridayAsync());
    }

    [Fact]
    public async Task CreateHold_AnotherCustomerForTheSameSlot_Is409_AndNothingIsHeldOrSent()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var supplier = await s.EnableBookingAsync();
        await s.Kit.Booking.CreateHoldAsync(supplier, await s.InputAsync());
        s.ForgetNotifications();

        var ex = await Assert.ThrowsAsync<DomainConflictException>(
            async () => await s.Kit.Booking.CreateHoldAsync(supplier, await s.InputAsync(email: "altra.persona@example.com")));

        Assert.Equal(ServiceRequestErrorCodes.SlotUnavailable, ex.Code);
        Assert.Equal("supplier_slot_unavailable", ex.Code);
        Assert.Single(await s.HoldsAsync());
        Assert.Empty(s.Emails.Snapshot());
    }

    [Fact]
    public async Task CreateHold_ASlotThatOverlapsTheHeldOne_OrTouchesItsBuffer_IsRefusedToo()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var supplier = await s.EnableBookingAsync();
        await s.Kit.Booking.CreateHoldAsync(supplier, await s.InputAsync());

        foreach (var start in new[] { ServiceRequestScenario.FridayAt10.AddHours(1), ServiceRequestScenario.FridayAt10.AddHours(-1) })
        {
            var ex = await Assert.ThrowsAsync<DomainConflictException>(
                async () => await s.Kit.Booking.CreateHoldAsync(supplier, await s.InputAsync(start, email: $"{start.Hour}@example.com")));
            Assert.Equal("supplier_slot_unavailable", ex.Code);
        }
    }

    [Fact]
    public async Task CreateHold_ASlotThePlannerDoesNotOffer_Is409_NotJustOneAlreadyHeld()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var supplier = await s.EnableBookingAsync();

        // Sunday: the supplier does not work. A time in the past. A time that is not on the grid of the slots.
        foreach (var start in new[]
                 {
                     ServiceRequestScenario.SundayAt10,
                     ServiceRequestScenario.FridayAt10.AddDays(-7),
                     ServiceRequestScenario.FridayAt10.AddMinutes(15),
                 })
        {
            var ex = await Assert.ThrowsAsync<DomainConflictException>(
                async () => await s.Kit.Booking.CreateHoldAsync(supplier, await s.InputAsync(start)));
            Assert.Equal("supplier_slot_unavailable", ex.Code);
        }

        Assert.Empty(await s.HoldsAsync());
    }

    [Fact]
    public async Task CreateHold_TheHoldCountsForTheMaximumJobsOfTheDay()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var supplier = await s.EnableBookingAsync();
        await s.Agenda.ReplaceRulesAsync(s.SupplierOrgId, new SupplierRulesInput(30, 2, 0, 35, 60));
        var early = ServiceRequestScenario.FridayAt10.AddHours(-2);
        var late = ServiceRequestScenario.FridayAt10.AddHours(1);
        Assert.Contains(late, await s.FreeSlotsOfFridayAsync());
        await s.Kit.Booking.CreateHoldAsync(supplier, await s.InputAsync(early, email: "uno@example.com"));
        await s.Kit.Booking.CreateHoldAsync(supplier, await s.InputAsync(ServiceRequestScenario.FridayAt14, email: "due@example.com"));

        // 08:00-10:00 and 14:00-16:00 (Rome) are held. 11:00-13:00 keeps the 30 minutes from both, but the day is full.
        Assert.DoesNotContain(late, await s.FreeSlotsOfFridayAsync());
        var ex = await Assert.ThrowsAsync<DomainConflictException>(
            async () => await s.Kit.Booking.CreateHoldAsync(supplier, await s.InputAsync(late, email: "tre@example.com")));

        Assert.Equal("supplier_slot_unavailable", ex.Code);
    }

    [Fact]
    public async Task CreateHold_ARequestWithHours_OrABlock_TakesTheSlotLikeAHold()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var supplier = await s.EnableBookingAsync();
        await s.RequestAsync(ServiceRequestScenario.FridayAt10);

        var ex = await Assert.ThrowsAsync<DomainConflictException>(
            async () => await s.Kit.Booking.CreateHoldAsync(supplier, await s.InputAsync()));

        Assert.Equal("supplier_slot_unavailable", ex.Code);
    }

    [Fact]
    public async Task CreateHold_TheSameClientRequestId_IsTheSameHold_NoSecondSlotAndNoSecondEmail()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var supplier = await s.EnableBookingAsync();
        var clientRequestId = Guid.NewGuid();
        var first = await s.Kit.Booking.CreateHoldAsync(supplier, await s.InputAsync(clientRequestId: clientRequestId));
        s.ForgetNotifications();
        s.Clock.Advance(TimeSpan.FromMinutes(5));

        var second = await s.Kit.Booking.CreateHoldAsync(supplier, await s.InputAsync(clientRequestId: clientRequestId));

        Assert.Equal(first, second);
        Assert.Single(await s.HoldsAsync());
        Assert.Empty(s.Emails.Snapshot());
    }

    [Fact]
    public async Task CreateHold_TheSameClientRequestId_OfAnotherSupplier_IsAnotherHold()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var supplier = await s.EnableBookingAsync();
        var other = await s.AddBookableSupplierAsync();
        var clientRequestId = Guid.NewGuid();

        var mine = await s.Kit.Booking.CreateHoldAsync(supplier, await s.InputAsync(clientRequestId: clientRequestId));
        var theirs = await s.Kit.Booking.CreateHoldAsync(
            other.Profile, await s.InputAsync(clientRequestId: clientRequestId, service: other.ServiceSlug));

        Assert.NotEqual(mine.Id, theirs.Id);
        Assert.Single(await s.HoldsAsync());
        Assert.Single(await s.HoldsAsync(other.OrgId));
    }

    [Fact]
    public async Task CreateHold_AClientRequestIdWhoseHoldLapsed_MakesANewHold_AndTheOldOneGoes()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var supplier = await s.EnableBookingAsync();
        var clientRequestId = Guid.NewGuid();
        var first = await s.Kit.Booking.CreateHoldAsync(supplier, await s.InputAsync(clientRequestId: clientRequestId));
        s.Clock.Advance(HoldMinutes + TimeSpan.FromMinutes(1));
        s.ForgetNotifications();

        var second = await s.Kit.Booking.CreateHoldAsync(supplier, await s.InputAsync(clientRequestId: clientRequestId));

        Assert.NotEqual(first.Id, second.Id);
        var holds = await s.HoldsAsync();
        Assert.Equal(second.Id, Assert.Single(holds).Id);
        Assert.Single(s.Emails.Snapshot());
    }

    [Fact]
    public async Task CreateHold_TheFourthWaitingBookingOfOneAddress_IsRefused_WithTheTimeToWait()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var supplier = await s.EnableBookingAsync();
        // Three different days, so the slots are free and only the address is the reason.
        var days = new[] { 0, 3, 4 }.Select(offset => ServiceRequestScenario.FridayAt10.AddDays(offset)).ToList();
        foreach (var day in days)
            await s.Kit.Booking.CreateHoldAsync(supplier, await s.InputAsync(day));
        s.Clock.Advance(TimeSpan.FromMinutes(10));
        s.ForgetNotifications();

        var ex = await Assert.ThrowsAsync<ShowcaseBookingTooManyHoldsException>(
            async () => await s.Kit.Booking.CreateHoldAsync(supplier, await s.InputAsync(ServiceRequestScenario.FridayAt10.AddDays(5))));

        // The first of the three lapses in 20 minutes: that is when a new booking is possible.
        Assert.Equal(TimeSpan.FromMinutes(20), ex.RetryAfter);
        Assert.Equal(3, (await s.HoldsAsync()).Count);
        Assert.Empty(s.Emails.Snapshot());
    }

    [Fact]
    public async Task CreateHold_TheCapIsOfTheAddress_AndOfTheSupplier_AndTheSpellingDoesNotMatter()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var supplier = await s.EnableBookingAsync();
        var other = await s.AddBookableSupplierAsync();
        foreach (var offset in new[] { 0, 3, 4 })
        {
            await s.Kit.Booking.CreateHoldAsync(
                supplier,
                await s.InputAsync(ServiceRequestScenario.FridayAt10.AddDays(offset), email: offset == 3 ? "MARIO.ROSSI@Example.com" : ShowcaseScenario.CustomerEmail));
        }

        // Another address of the same supplier, and the same address at another supplier, are not capped.
        await s.Kit.Booking.CreateHoldAsync(
            supplier, await s.InputAsync(ServiceRequestScenario.FridayAt10.AddDays(5), email: "altra.persona@example.com"));
        await s.Kit.Booking.CreateHoldAsync(other.Profile, await s.InputAsync(service: other.ServiceSlug));

        await Assert.ThrowsAsync<ShowcaseBookingTooManyHoldsException>(
            async () => await s.Kit.Booking.CreateHoldAsync(supplier, await s.InputAsync(ServiceRequestScenario.FridayAt10.AddDays(6))));
    }

    [Fact]
    public async Task CreateHold_ABookingWhoseAddressWasChecked_NoLongerCountsForTheCap()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var supplier = await s.EnableBookingAsync();
        var first = await s.HoldAsync(await s.InputAsync(ServiceRequestScenario.FridayAt10));
        await s.HoldAsync(await s.InputAsync(ServiceRequestScenario.FridayAt10.AddDays(3)));
        await s.HoldAsync(await s.InputAsync(ServiceRequestScenario.FridayAt10.AddDays(4)));
        await s.Kit.Booking.ConfirmEmailAsync(supplier, first.Hold.Id, first.Token);

        var fourth = await s.Kit.Booking.CreateHoldAsync(supplier, await s.InputAsync(ServiceRequestScenario.FridayAt10.AddDays(5)));

        Assert.NotEqual(Guid.Empty, fourth.Id);
    }

    [Fact]
    public async Task CreateHold_AHoldThatLapsed_NoLongerCountsForTheCap()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var supplier = await s.EnableBookingAsync();
        foreach (var offset in new[] { 0, 3, 4 })
            await s.Kit.Booking.CreateHoldAsync(supplier, await s.InputAsync(ServiceRequestScenario.FridayAt10.AddDays(offset)));
        s.Clock.Advance(HoldMinutes + TimeSpan.FromSeconds(1));

        var fresh = await s.Kit.Booking.CreateHoldAsync(supplier, await s.InputAsync());

        Assert.NotEqual(Guid.Empty, fresh.Id);
    }

    [Fact]
    public async Task CreateHold_NoConsent_OrAnOldVersionOfTheNotice_Is422_AndNothingIsHeld()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var supplier = await s.EnableBookingAsync();

        var none = await Assert.ThrowsAsync<DomainRuleException>(
            async () => await s.Kit.Booking.CreateHoldAsync(supplier, await s.InputAsync(change: i => i with { PrivacyAccepted = false })));
        var old = await Assert.ThrowsAsync<DomainRuleException>(
            async () => await s.Kit.Booking.CreateHoldAsync(supplier, await s.InputAsync(change: i => i with { PrivacyNoticeVersion = "2026-01-vecchia" })));

        Assert.Equal("supplier_booking_consent_required", none.Code);
        Assert.Equal("supplier_booking_consent_outdated", old.Code);
        Assert.Empty(await s.HoldsAsync());
    }

    [Fact]
    public async Task CreateHold_WithoutAConfiguredVersionOfTheNotice_FailsClosed()
    {
        using var s = await ServiceRequestScenario.CreateAsync(showcaseOptions: new ShowcaseBookingOptions());
        var supplier = await s.EnableBookingAsync();

        var ex = await Assert.ThrowsAsync<DomainRuleException>(async () => await s.Kit.Booking.CreateHoldAsync(supplier, await s.InputAsync()));

        Assert.Equal("supplier_booking_consent_outdated", ex.Code);
        Assert.Empty(await s.HoldsAsync());
    }

    [Fact]
    public async Task CreateHold_Values_ThatAreNotValid_AreRefusedTogether_WithTheirFields()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var supplier = await s.EnableBookingAsync();

        var ex = await Assert.ThrowsAsync<ShowcaseBookingRuleException>(
            async () => await s.Kit.Booking.CreateHoldAsync(
                supplier, await s.InputAsync(change: i => i with { Email = "non-un-indirizzo", PostalCode = "12", FullName = " " })));

        Assert.Equal(["postalCode", "fullName", "email"], ex.Fields);
        Assert.Empty(await s.HoldsAsync());
    }

    [Fact]
    public async Task CreateHold_AChoiceThatDoesNotFitTheService_Is422_OfTheEstimate()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var supplier = await s.EnableBookingAsync();

        var ex = await Assert.ThrowsAsync<SupplierQuoteRuleException>(
            async () => await s.Kit.Booking.CreateHoldAsync(
                supplier, await s.InputAsync(change: i => i with { Options = [new SupplierQuoteOption("non-esiste", 1)] })));

        Assert.Equal("supplier_quote_invalid", ex.Code);
        Assert.Equal(["options[0].code"], ex.Fields);
        Assert.Empty(await s.HoldsAsync());
    }

    [Fact]
    public async Task CreateHold_AComuneTheSupplierDoesNotCover_Is422_AskForAQuote()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var supplier = await s.EnableBookingAsync();

        var ex = await Assert.ThrowsAsync<DomainRuleException>(
            async () => await s.Kit.Booking.CreateHoldAsync(supplier, await s.InputAsync(change: i => i with { City = "Napoli" })));

        Assert.Equal("supplier_booking_outside_zone", ex.Code);
        Assert.Equal("SupplierBookingOutsideZone", ex.MessageKey);
        Assert.Empty(await s.HoldsAsync());
    }

    [Fact]
    public async Task CreateHold_TheZoneIsAComune_NotAPostalCode()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var supplier = await s.EnableBookingAsync();

        // Any postal code in a comune the supplier covers; the name is compared as the matcher always does (case, accents).
        var hold = await s.Kit.Booking.CreateHoldAsync(
            supplier, await s.InputAsync(change: i => i with { City = " MONZA ", PostalCode = "00100" }));

        Assert.NotEqual(Guid.Empty, hold.Id);
    }

    [Fact]
    public async Task CreateHold_AnIstatCode_IsCheckedAgainstTheCodesTheSupplierChose()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        await s.EnableBookingAsync();
        // The supplier works in Milano only, chosen from the official list (no comune written by name).
        var profile = await s.Db.SupplierProfiles.SingleAsync(sp => sp.OrgId == s.SupplierOrgId);
        profile.ComuniJson = "[]";
        profile.ComuneIstatCodesJson = $"[\"{ComuneTestData.Milano}\"]";
        await s.Db.SaveChangesAsync();
        s.Db.ChangeTracker.Clear();
        var supplier = await s.SupplierAsync();

        var covered = await s.Kit.Booking.CreateHoldAsync(
            supplier, await s.InputAsync(change: i => i with { ComuneIstat = ComuneTestData.Milano, City = "Milano" }));
        var ex = await Assert.ThrowsAsync<DomainRuleException>(
            async () => await s.Kit.Booking.CreateHoldAsync(
                supplier,
                await s.InputAsync(
                    ServiceRequestScenario.FridayAt14,
                    email: "x@example.com",
                    change: i => i with { ComuneIstat = ComuneTestData.Roma, City = "Roma" })));

        Assert.NotEqual(Guid.Empty, covered.Id);
        Assert.Equal("supplier_booking_outside_zone", ex.Code);
    }

    [Fact]
    public async Task CreateHold_ASupplierThatDoesNotTakeBookingsOnline_Is422()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var supplier = await s.EnableBookingAsync(onlineBooking: false);

        var ex = await Assert.ThrowsAsync<DomainRuleException>(async () => await s.Kit.Booking.CreateHoldAsync(supplier, await s.InputAsync()));

        Assert.Equal("supplier_booking_offline", ex.Code);
        Assert.Empty(await s.HoldsAsync());
    }

    [Fact]
    public async Task CreateHold_ASupplierThatNeverSavedASetting_DoesNotTakeBookingsOnline()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var supplier = await s.EnableBookingAsync();
        // A supplier with no settings row at all has the defaults, which are "off".
        s.Db.SupplierSettings.RemoveRange(await s.Db.SupplierSettings.Where(x => x.OrgId == s.SupplierOrgId).ToListAsync());
        await s.Db.SaveChangesAsync();
        s.Db.ChangeTracker.Clear();

        var ex = await Assert.ThrowsAsync<DomainRuleException>(async () => await s.Kit.Booking.CreateHoldAsync(supplier, await s.InputAsync()));

        Assert.Equal("supplier_booking_offline", ex.Code);
    }

    [Fact]
    public async Task CreateHold_AServiceThatIsNotPublished_OrIsAnotherSuppliers_IsTheSame404()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var supplier = await s.EnableBookingAsync();
        var other = await s.AddBookableSupplierAsync();
        var draft = await s.ListingSlugAsync(await s.AddListingAsync("Bozza", status: SupplierServiceListingStatus.Draft));
        var paused = await s.ListingSlugAsync(await s.AddListingAsync("In pausa", status: SupplierServiceListingStatus.Paused));
        var deleted = await s.ListingSlugAsync(await s.AddListingAsync("Eliminato", deletedAt: s.Clock.GetUtcNow().UtcDateTime));

        foreach (var slug in new[] { "non-esiste", draft, paused, deleted, other.ServiceSlug, "x\u0000y", new string('a', 300) })
        {
            var ex = await Assert.ThrowsAsync<NotFoundException>(
                async () => await s.Kit.Booking.CreateHoldAsync(supplier, await s.InputAsync(service: slug)));
            Assert.Equal("supplier_service_not_found", ex.Code);
        }

        Assert.Empty(await s.HoldsAsync());
    }

    [Fact]
    public async Task CreateHold_ASupplierThatIsNotActive_IsTheShowcase404()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var supplier = await s.EnableBookingAsync();
        await s.SetSupplierStatusAsync(SupplierStatus.Suspended);
        supplier = await s.SupplierAsync();

        var ex = await Assert.ThrowsAsync<NotFoundException>(async () => await s.Kit.Booking.CreateHoldAsync(supplier, await s.InputAsync()));

        Assert.Equal("not_found", ex.Code);
        Assert.Equal("SupplierShowcaseNotFound", ex.MessageKey);
    }

    [Fact]
    public async Task CreateHold_TheServiceOnQuote_IsBookedWithoutAnEstimate()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var supplier = await s.EnableBookingAsync();
        var onQuote = await s.AddListingAsync("Su preventivo", priceFromCents: null, requiresQuote: true);

        await s.Kit.Booking.CreateHoldAsync(supplier, await s.InputAsync(service: await s.ListingSlugAsync(onQuote)));

        var payload = ShowcaseBookingPayload.FromJson(Assert.Single(await s.HoldsAsync()).Payload)!;
        Assert.Null(payload.EstimatedAmountCents);
    }

    [Fact]
    public async Task CreateHold_TheEstimate_IsThePriceOfTheCalculator_WithItsOptionsKeptAsASnapshot()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var supplier = await s.EnableBookingAsync();
        await s.SetSupplementsAsync(
            s.ListingId,
            new SupplierServiceSupplement("bagno", "Bagno in più", 1500, SupplierServiceSupplementUnits.Bathroom, 3),
            new SupplierServiceSupplement("pesanti", "Pulizie pesanti", 2000, SupplierServiceSupplementUnits.Flat, null),
            new SupplierServiceSupplement("metri", "Ogni 30 m² oltre 60", 1000, SupplierServiceSupplementUnits.Sqm30, 4));

        await s.Kit.Booking.CreateHoldAsync(
            supplier,
            await s.InputAsync(change: i => i with
            {
                SurfaceSqm = 100,
                Options = [new SupplierQuoteOption("bagno", 2), new SupplierQuoteOption("pesanti", null)],
            }));

        var payload = ShowcaseBookingPayload.FromJson(Assert.Single(await s.HoldsAsync()).Payload)!;
        // 60.00 base + 2 bathrooms at 15.00 + the flat 20.00 + two blocks of 30 m² above 60 (100 m²: 40 over, a started block counts whole).
        Assert.Equal(6000 + 2 * 1500 + 2000 + 2 * 1000, payload.EstimatedAmountCents);
        Assert.Equal(
            [
                new ServiceRequestOption("bagno", "Bagno in più", 1500, "bathroom", 2),
                new ServiceRequestOption("pesanti", "Pulizie pesanti", 2000, "flat", 1),
                new ServiceRequestOption("metri", "Ogni 30 m² oltre 60", 1000, "sqm30", 2),
            ],
            payload.Options);
    }

    [Fact]
    public async Task CreateHold_APricePerHour_KeepsTheHoursTheCustomerChose()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var supplier = await s.EnableBookingAsync();
        var hourly = await s.AddListingAsync("A ore", priceFromCents: 2500, priceUnit: SupplierServicePriceUnit.PerHour, durationMinutes: 180);

        await s.Kit.Booking.CreateHoldAsync(
            supplier, await s.InputAsync(service: await s.ListingSlugAsync(hourly), change: i => i with { Quantity = 3 }));

        var payload = ShowcaseBookingPayload.FromJson(Assert.Single(await s.HoldsAsync()).Payload)!;
        Assert.Equal(7500, payload.EstimatedAmountCents);
        Assert.Equal([new ServiceRequestOption("quantity", "A ore", 2500, "hour", 3)], payload.Options);
        // The slot is as long as the service: three hours.
        Assert.Equal(TimeSpan.FromHours(3), Assert.Single(await s.HoldsAsync()).EndUtc - ServiceRequestScenario.FridayAt10);
    }

    [Fact]
    public async Task CreateHold_TheCodeOfTwoBookings_AreDifferent()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var supplier = await s.EnableBookingAsync();

        await s.Kit.Booking.CreateHoldAsync(supplier, await s.InputAsync());
        await s.Kit.Booking.CreateHoldAsync(supplier, await s.InputAsync(ServiceRequestScenario.FridayAt14, email: "x@example.com"));

        var codes = (await s.HoldsAsync()).Select(h => h.PublicCode).ToList();
        Assert.Equal(2, codes.Distinct().Count());
        Assert.All(codes, code => Assert.True(BookingCodes.TryNormalize(code, out _)));
    }

    [Fact]
    public async Task CreateHold_AMissingPublicSiteUrl_StopsTheBooking_BeforeAnythingIsHeld()
    {
        using var s = await ServiceRequestScenario.CreateAsync(publicSiteBaseUrl: null);
        var supplier = await s.EnableBookingAsync();

        await Assert.ThrowsAsync<Casazen.Infrastructure.Email.EmailConfigurationException>(
            async () => await s.Kit.Booking.CreateHoldAsync(supplier, await s.InputAsync()));

        Assert.Empty(await s.HoldsAsync());
    }

    [Fact]
    public async Task CreateHold_AnEmailThatCannotBeQueued_ReleasesTheSlot_AndFails_SoTheCustomerCanTryAgain()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var supplier = await s.EnableBookingAsync();
        var clientRequestId = Guid.NewGuid();
        s.Emails.Refuse = true;

        await Assert.ThrowsAsync<Casazen.Infrastructure.Email.EmailConfigurationException>(
            async () => await s.Kit.Booking.CreateHoldAsync(supplier, await s.InputAsync(clientRequestId: clientRequestId)));

        // Nothing is left holding the slot, and nothing was sent.
        Assert.Empty(await s.HoldsAsync());
        Assert.Contains(ServiceRequestScenario.FridayAt10, await s.FreeSlotsOfFridayAsync());
        Assert.Empty(s.Emails.Snapshot());

        // The same attempt, once the queue works again, is a booking like any other.
        s.Emails.Refuse = false;
        var hold = await s.Kit.Booking.CreateHoldAsync(supplier, await s.InputAsync(clientRequestId: clientRequestId));
        Assert.Single(s.Emails.Snapshot(), e => e.Template == EmailTemplates.Names.SupplierBookingVerification);
        Assert.Equal(hold.Id, Assert.Single(await s.HoldsAsync()).Id);
    }

    [Fact]
    public async Task CreateHold_TheEmailVerificationMinutes_AreConfigurable()
    {
        using var s = await ServiceRequestScenario.CreateAsync(
            showcaseOptions: new Casazen.Core.Options.ShowcaseBookingOptions
            {
                PrivacyNoticeVersion = ServiceRequestTestKit.PrivacyNoticeVersion,
                EmailVerificationMinutes = 10,
            });
        var supplier = await s.EnableBookingAsync();

        var hold = await s.Kit.Booking.CreateHoldAsync(supplier, await s.InputAsync());

        Assert.Equal(s.Clock.GetUtcNow().UtcDateTime.AddMinutes(10), hold.ExpiresAt);
        Assert.Contains("10 minuti", Assert.Single(s.Emails.Snapshot(), e => e.Template == EmailTemplates.Names.SupplierBookingVerification).Content.HtmlBody);
    }
}
