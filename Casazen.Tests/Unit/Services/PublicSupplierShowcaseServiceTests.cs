using System.Text.Json;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Services;
using Casazen.Core.Suppliers;
using Casazen.Core.Utilities;
using Casazen.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// SP-09: the anonymous read side of a supplier's showcase (<see cref="PublicSupplierShowcaseService"/>) on the world of the
/// service request scenario (an active supplier that works Monday to Friday 08-13 and 14-18 and Saturday 08-14, a two hours
/// service at 60 euro, buffer 30 minutes, 35 days ahead, a slot every hour; Thursday 8 October 2026, 12:00 in Rome): who is
/// visible, which services, the slots against the planner of SP-03 (parity, the requests with hours, the holds SP-10 adds),
/// the days without a slot that say nothing about why, the 30 seconds cache, and the estimate with the zones.
/// </summary>
public class PublicSupplierShowcaseServiceTests
{
    private const string Slug = "supplier-srl";

    private static readonly DateOnly Today = new(2026, 10, 8);

    // ─── Who is visible ──────────────────────────────────────────────────────────

    [Theory]
    [InlineData("supplier-srl")]
    [InlineData("SUPPLIER-SRL")]
    [InlineData("  Supplier-Srl  ")]
    public async Task FindActiveSupplierAsync_AnActiveSupplier_IsFoundByItsSlug_LowercaseAndTrimmed(string slug)
    {
        using var s = await WorldAsync();

        var supplier = await Showcase(s).FindActiveSupplierAsync(slug);

        Assert.NotNull(supplier);
        Assert.Equal(s.SupplierOrgId, supplier.OrgId);
    }

    [Theory]
    [InlineData(SupplierStatus.Pending)]
    [InlineData(SupplierStatus.Suspended)]
    public async Task FindActiveSupplierAsync_ASupplierThatIsNotActive_IsAsIfItDidNotExist(SupplierStatus status)
    {
        using var s = await WorldAsync();
        await s.SetSupplierStatusAsync(status);

        Assert.Null(await Showcase(s).FindActiveSupplierAsync(Slug));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("non-esiste")]
    public async Task FindActiveSupplierAsync_NoSlugOrAnUnknownOne_IsNull(string? slug)
    {
        using var s = await WorldAsync();

        Assert.Null(await Showcase(s).FindActiveSupplierAsync(slug));
    }

    [Fact]
    public async Task FindActiveSupplierAsync_ASlugLongerThanAnyColumn_IsNullWithoutALookup()
    {
        using var s = await WorldAsync();
        var showcase = Showcase(s);
        await s.Db.DisposeAsync(); // any lookup fails from here: the answer for such a slug must not need one

        Assert.Null(await showcase.FindActiveSupplierAsync(new string('a', 101)));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => showcase.FindActiveSupplierAsync(Slug));
    }

    // ─── Services ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ListServicesAsync_OnlyThePublishedServicesOfTheSupplier_InTheSuppliersOrder()
    {
        using var s = await WorldAsync();
        var other = await s.AddOtherSupplierAsync();
        await s.AddListingAsync("Bozza", status: SupplierServiceListingStatus.Draft);
        await s.AddListingAsync("In pausa", status: SupplierServiceListingStatus.Paused);
        await s.AddListingAsync("Eliminato", deletedAt: ServiceRequestScenario.Instant.UtcDateTime);
        await s.AddListingAsync("Di un altro", supplierOrgId: other);
        var second = await s.AddListingAsync("Seconda");
        await SetSortOrderAsync(s, second, -1);

        var services = await Showcase(s).ListServicesAsync(await SupplierAsync(s));

        Assert.Equal(new[] { "Seconda", ServiceRequestScenario.ServiceName }, services.Select(x => x.Name));
    }

    [Fact]
    public async Task ListServicesAsync_TwoSuppliersWithTheSameServiceSlug_EachSeesItsOwn()
    {
        using var s = await WorldAsync();
        var other = await s.AddOtherSupplierAsync();
        await SetShowcaseSlugAsync(s, other, "altro-srl");
        var mine = await s.AddListingAsync("Pulizia", priceFromCents: 6000);
        var theirs = await s.AddListingAsync("Pulizia", priceFromCents: 9900, supplierOrgId: other);
        await SetListingSlugAsync(s, mine, "pulizia");
        await SetListingSlugAsync(s, theirs, "pulizia");
        var showcase = Showcase(s);

        var a = await showcase.FindServiceAsync(await SupplierAsync(s), "pulizia");
        var b = await showcase.FindServiceAsync((await showcase.FindActiveSupplierAsync("altro-srl"))!, "pulizia");

        Assert.Equal(6000, a!.PriceFromCents);
        Assert.Equal(9900, b!.PriceFromCents);
    }

    [Fact]
    public async Task FindServiceAsync_APublishedService_IsFoundByItsSlug_LowercaseAndTrimmed()
    {
        using var s = await WorldAsync();
        var slug = await SlugOfAsync(s, s.ListingId);

        var service = await Showcase(s).FindServiceAsync(await SupplierAsync(s), $"  {slug.ToUpperInvariant()} ");

        Assert.NotNull(service);
        Assert.Equal(ServiceRequestScenario.ServiceName, service.Name);
        Assert.Equal(ServiceRequestScenario.ServicePriceCents, service.PriceFromCents);
        Assert.Equal(ServiceRequestScenario.ServiceMinutes, service.DurationMinutes);
        Assert.False(service.PricesIncludeVat);
    }

    [Theory]
    [InlineData(SupplierServiceListingStatus.Draft, false)]
    [InlineData(SupplierServiceListingStatus.Paused, false)]
    [InlineData(SupplierServiceListingStatus.Active, true)]
    public async Task FindServiceAsync_OnlyAnActiveService_IsFound(SupplierServiceListingStatus status, bool found)
    {
        using var s = await WorldAsync();
        var id = await s.AddListingAsync("Altro", status: status);

        var service = await Showcase(s).FindServiceAsync(await SupplierAsync(s), await SlugOfAsync(s, id));

        Assert.Equal(found, service is not null);
    }

    [Fact]
    public async Task FindServiceAsync_ADeletedService_AnotherSuppliersAndAnUnknownSlug_AreAllNull()
    {
        using var s = await WorldAsync();
        var other = await s.AddOtherSupplierAsync();
        var deleted = await s.AddListingAsync("Eliminato", deletedAt: ServiceRequestScenario.Instant.UtcDateTime);
        var foreign = await s.AddListingAsync("Di un altro", supplierOrgId: other);
        var showcase = Showcase(s);
        var supplier = await SupplierAsync(s);

        Assert.Null(await showcase.FindServiceAsync(supplier, await SlugOfAsync(s, deleted)));
        Assert.Null(await showcase.FindServiceAsync(supplier, await SlugOfAsync(s, foreign)));
        Assert.Null(await showcase.FindServiceAsync(supplier, "non-esiste"));
        Assert.Null(await showcase.FindServiceAsync(supplier, null));
        Assert.Null(await showcase.FindServiceAsync(supplier, "   "));
        Assert.Null(await showcase.FindServiceAsync(supplier, new string('a', 81)));
    }

    [Fact]
    public async Task ListServicesAsync_ASupplierThatStoppedBeingActive_HasNoPublicService_EvenForACallerThatHoldsAnOldProfile()
    {
        using var s = await WorldAsync();
        var supplier = await SupplierAsync(s);
        await s.SetSupplierStatusAsync(SupplierStatus.Suspended);

        // The status is part of the statement that reads the services: a stale profile in the hands of the caller is not enough.
        Assert.Empty(await Showcase(s).ListServicesAsync(supplier));
        Assert.Null(await Showcase(s).FindServiceAsync(supplier, await SlugOfAsync(s, s.ListingId)));
    }

    // ─── Extension of the page ───────────────────────────────────────────────────

    [Fact]
    public async Task GetExtensionAsync_NoRequestsAnswered_HasTheServicesAndNoResponseTime()
    {
        using var s = await WorldAsync();

        var extension = await Showcase(s).GetExtensionAsync(await SupplierAsync(s));

        Assert.Single(extension.Services);
        Assert.Null(extension.MedianResponseMinutes);
    }

    [Fact]
    public async Task GetExtensionAsync_TheResponseTime_IsTheMedianOverTheLastNinetyDays_OnceThereAreFiveAnswers()
    {
        using var s = await WorldAsync();
        var other = await s.AddOtherSupplierAsync();
        var now = ServiceRequestScenario.Instant.UtcDateTime;
        foreach (var minutes in new[] { 5, 10, 20, 30 })
            await s.SeedAsync(ServiceRequestStatus.PresoInCarico, r => Taken(r, now.AddDays(-3), minutes));
        // Not answers of this supplier, or not in the window, or never taken: they never count.
        await s.SeedAsync(ServiceRequestStatus.Completato, r => Taken(r, now.AddDays(-95), 60)); // taken 95 days ago
        await s.SeedAsync(ServiceRequestStatus.PresoInCarico, r => Taken(r, now.AddDays(-3), 1), supplierOrgId: other);
        await s.SeedAsync(ServiceRequestStatus.Richiesto);
        Assert.Null((await Showcase(s).GetExtensionAsync(await SupplierAsync(s))).MedianResponseMinutes); // four: not enough

        await s.SeedAsync(ServiceRequestStatus.PresoInCarico, r => Taken(r, now.AddDays(-1), 600));

        Assert.Equal(20, (await Showcase(s).GetExtensionAsync(await SupplierAsync(s))).MedianResponseMinutes); // 5 10 20 30 600
    }

    // ─── Slots ───────────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetSlotsAsync_TheSlotsOfAFreshAgenda_AreExactlyWhatThePlannerOfTheAgendaComputesForTheSameInput()
    {
        using var s = await WorldAsync();
        var slug = await SlugOfAsync(s, s.ListingId);
        var query = new SupplierSlotQuery(ServiceRequestScenario.ServiceMinutes, null, SupplierServiceWeekdays.AllMask);

        var slots = await Showcase(s).GetSlotsAsync(await SupplierAsync(s), slug, from: null, days: null);

        var input = await s.Agenda.BuildPlanningInputAsync(s.SupplierOrgId, Today, Today.AddDays(13));
        var expected = SupplierSlotPlanner.PlanRange(Today, Today.AddDays(13), input, query);
        Assert.NotNull(slots);
        Assert.Equal(expected.Select(d => d.Day), slots.Days.Select(d => d.Date));
        Assert.Equal(expected.Select(d => d.Slots), slots.Days.Select(d => d.Slots));
        Assert.Equal(slug, slots.ServiceSlug);
        Assert.Equal(120, slots.DurationMinutes);
        Assert.Equal(Today.AddDays(35), slots.BookableUntil);
        Assert.Contains(slots.Days, d => d.Available);
    }

    [Fact]
    public async Task GetSlotsAsync_ARequestWithHours_TakesItsSlotsAndTheBufferAround_ForAnyVisitor()
    {
        using var s = await WorldAsync();
        var slug = await SlugOfAsync(s, s.ListingId);
        await s.RequestAsync(start: ServiceRequestScenario.FridayAt10); // Friday 9 October 10:00-12:00 in Rome

        var slots = await Showcase(s).GetSlotsAsync(await SupplierAsync(s), slug, from: new DateOnly(2026, 10, 9), days: 1);

        var friday = Assert.Single(slots!.Days);
        Assert.Equal(new DateOnly(2026, 10, 9), friday.Date);
        // 10:00-12:00 plus 30 minutes before and after: 09:30-12:30 Rome (07:30-10:30 UTC). Every slot of the morning
        // (08, 09, 10 and 11 o'clock, two hours long) touches it; the afternoon is free.
        var taken = (Start: new DateTime(2026, 10, 9, 7, 30, 0, DateTimeKind.Utc), End: new DateTime(2026, 10, 9, 10, 30, 0, DateTimeKind.Utc));
        Assert.All(friday.Slots, slot => Assert.True(slot.EndUtc <= taken.Start || slot.StartUtc >= taken.End, $"{slot} overlaps the job"));
        Assert.Equal(
            new[] { ServiceRequestScenario.FridayAt14, ServiceRequestScenario.FridayAt14.AddHours(1), ServiceRequestScenario.FridayAt14.AddHours(2) },
            friday.Slots.Select(slot => slot.StartUtc));
    }

    [Fact]
    public async Task GetSlotsAsync_AHoldOfAnotherCustomer_TakesItsSlots_AnExpiredOneDoesNot()
    {
        using var s = await WorldAsync();
        var slug = await SlugOfAsync(s, s.ListingId);
        var friday10 = ServiceRequestScenario.FridayAt10; // 10:00 Rome
        var holds = new List<SupplierOccupancy>
        {
            // SP-10 will put a hold here (a booking waiting for the e-mail check); it is a planner input like any other.
            SupplierOccupancy.Hold(friday10, friday10.AddHours(2), ServiceRequestScenario.Instant.UtcDateTime.AddMinutes(30)),
            SupplierOccupancy.Hold(ServiceRequestScenario.FridayAt14, ServiceRequestScenario.FridayAt14.AddHours(2), ServiceRequestScenario.Instant.UtcDateTime.AddMinutes(-1)),
        };
        var probe = new AgendaProbe(s.Agenda) { Extra = holds };

        var slots = await Showcase(s, probe).GetSlotsAsync(await SupplierAsync(s), slug, new DateOnly(2026, 10, 9), 1);

        var friday = Assert.Single(slots!.Days);
        Assert.DoesNotContain(friday.Slots, slot => slot.StartUtc < friday10.AddHours(2).AddMinutes(30) && slot.EndUtc > friday10.AddMinutes(-30));
        // The expired hold frees its slot again.
        Assert.Contains(friday.Slots, slot => slot.StartUtc == ServiceRequestScenario.FridayAt14);
    }

    [Fact]
    public async Task GetSlotsAsync_ADayWithoutSlots_SaysNothingAboutWhy_AndNoPrivateTextGoesPastThePlanner()
    {
        using var s = await WorldAsync();
        var slug = await SlugOfAsync(s, s.ListingId);
        // Sunday (no hours), time off with a label, a block with a label, a day closed by hand: four reasons, one answer.
        await s.Agenda.AddTimeOffAsync(s.SupplierOrgId, new SupplierTimeOffInput(new DateOnly(2026, 10, 12), new DateOnly(2026, 10, 12), SupplierTimeOffReason.Illness, "Operazione segreta"));
        await s.Agenda.AddBlockAsync(s.SupplierOrgId, new SupplierBlockInput(
            SupplierBusyWindowKind.Block,
            new DateTime(2026, 10, 13, 6, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 10, 13, 16, 0, 0, DateTimeKind.Utc),
            "Dentista riservato"));
        s.Db.SupplierAvailability.Add(new SupplierAvailability { OrgId = s.SupplierOrgId, Date = new DateOnly(2026, 10, 14), Available = false, Source = SupplierAvailabilitySource.Manual });
        await s.Db.SaveChangesAsync();

        var slots = await Showcase(s).GetSlotsAsync(await SupplierAsync(s), slug, new DateOnly(2026, 10, 11), 5);

        Assert.NotNull(slots);
        Assert.Equal(5, slots.Days.Count);
        // 11 Sunday, 12 time off, 13 blocked 08:00-18:00, 14 closed by hand: all "not available", with no slot.
        Assert.All(slots.Days.Take(4), day =>
        {
            Assert.False(day.Available);
            Assert.Empty(day.Slots);
        });
        Assert.True(slots.Days[4].Available); // Friday 15 October works as usual
        var json = JsonSerializer.Serialize(slots);
        Assert.DoesNotContain("segreta", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Dentista", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PublicSlotTypes_HaveNoFieldForAReasonAKindOrALabel()
    {
        var forbidden = new[] { "Closure", "Reason", "Label", "Kind", "Source", "Uid", "Note", "Request", "Customer" };

        foreach (var type in new[] { typeof(PublicSlots), typeof(PublicSlotDay), typeof(SupplierSlot), typeof(PublicSlotPlan) })
        {
            var names = type.GetProperties().Select(p => p.Name).ToList();
            Assert.All(forbidden, word => Assert.DoesNotContain(names, name => name.Contains(word, StringComparison.OrdinalIgnoreCase)));
        }
    }

    [Fact]
    public async Task GetSlotsAsync_TheWindow_StartsNoEarlierThanToday_AndIsCutAtTheHorizon()
    {
        using var s = await WorldAsync();
        var slug = await SlugOfAsync(s, s.ListingId);
        var supplier = await SupplierAsync(s);
        var showcase = Showcase(s);

        var past = await showcase.GetSlotsAsync(supplier, slug, new DateOnly(2026, 9, 1), 3);
        var nearTheEnd = await showcase.GetSlotsAsync(supplier, slug, Today.AddDays(33), 14);
        var lastDay = await showcase.GetSlotsAsync(supplier, slug, Today.AddDays(35), 14);
        var beyond = await showcase.GetSlotsAsync(supplier, slug, Today.AddDays(36), 14);
        var absurd = await showcase.GetSlotsAsync(supplier, slug, DateOnly.MaxValue, 62);

        Assert.Equal(new[] { Today, Today.AddDays(1), Today.AddDays(2) }, past!.Days.Select(d => d.Date));
        Assert.Equal(new[] { Today.AddDays(33), Today.AddDays(34), Today.AddDays(35) }, nearTheEnd!.Days.Select(d => d.Date));
        Assert.Equal(Today.AddDays(35), Assert.Single(lastDay!.Days).Date);
        Assert.Empty(beyond!.Days);
        Assert.Equal(Today.AddDays(35), beyond.BookableUntil);
        Assert.Empty(absurd!.Days);
    }

    [Theory]
    [InlineData(null, 14)]
    [InlineData(1, 1)]
    [InlineData(62, 36)] // 62 days asked, but the horizon ends first: today plus 35 days is the 36th day
    [InlineData(200, 36)] // more than the most a read covers is read as the most
    [InlineData(0, 1)]
    [InlineData(-4, 1)]
    public async Task GetSlotsAsync_TheNumberOfDays_DefaultsTo14_AndStaysBetweenOneAndTheHorizon(int? days, int listed)
    {
        using var s = await WorldAsync();

        var slots = await Showcase(s).GetSlotsAsync(await SupplierAsync(s), await SlugOfAsync(s, s.ListingId), null, days);

        Assert.Equal(listed, slots!.Days.Count);
    }

    [Fact]
    public async Task GetSlotsAsync_TheLongestHorizonASupplierCanSet_365Days_IsPlannedAndPagedWithoutError()
    {
        using var s = await WorldAsync();
        await s.Agenda.ReplaceRulesAsync(s.SupplierOrgId, new SupplierRulesInput(30, 3, 0, 365, 60));
        var showcase = Showcase(s);
        var supplier = await SupplierAsync(s);
        var slug = await SlugOfAsync(s, s.ListingId);

        var first = await showcase.GetSlotsAsync(supplier, slug, null, 62);
        var last = await showcase.GetSlotsAsync(supplier, slug, Today.AddDays(300), 62);

        Assert.Equal(Today.AddDays(365), first!.BookableUntil);
        Assert.Equal(62, first.Days.Count);
        Assert.Equal(Today.AddDays(300), last!.Days[0].Date);
        Assert.Equal(Today.AddDays(361), last.Days[^1].Date);
        Assert.Contains(last.Days, d => d.Available);
    }

    [Fact]
    public async Task GetSlotsAsync_AServiceThatIsNotPublished_IsNull()
    {
        using var s = await WorldAsync();
        var draft = await s.AddListingAsync("Bozza", status: SupplierServiceListingStatus.Draft);

        var slots = await Showcase(s).GetSlotsAsync(await SupplierAsync(s), await SlugOfAsync(s, draft), null, null);

        Assert.Null(slots);
    }

    [Fact]
    public async Task GetSlotsAsync_TheServiceOwnTerms_NoticeAndWeekdays_ShapeTheSlots()
    {
        using var s = await WorldAsync();
        var mondaysOnly = await s.AddListingAsync("Solo lunedì", weekdaysMask: SupplierServiceWeekdays.ToMask([DayOfWeek.Monday]));

        var slots = await Showcase(s).GetSlotsAsync(await SupplierAsync(s), await SlugOfAsync(s, mondaysOnly), Today, 14);

        Assert.All(slots!.Days.Where(d => d.Available), day => Assert.Equal(DayOfWeek.Monday, day.Date.DayOfWeek));
        Assert.Contains(slots.Days, d => d.Available);
    }

    // ─── The cache ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetSlotsAsync_TheSamePlanIsServedFor30Seconds_ThenComputedAgain()
    {
        using var s = await WorldAsync();
        var probe = new AgendaProbe(s.Agenda);
        var showcase = Showcase(s, probe);
        var supplier = await SupplierAsync(s);
        var slug = await SlugOfAsync(s, s.ListingId);

        await showcase.GetSlotsAsync(supplier, slug, null, null);
        await showcase.GetSlotsAsync(supplier, slug, null, null);
        // Another window of the same plan is a slice of it, not a new computation.
        await showcase.GetSlotsAsync(supplier, slug, Today.AddDays(5), 3);
        Assert.Equal(1, probe.PlanCalls);

        s.Clock.Advance(TimeSpan.FromSeconds(29));
        await showcase.GetSlotsAsync(supplier, slug, null, null);
        Assert.Equal(1, probe.PlanCalls);

        s.Clock.Advance(TimeSpan.FromSeconds(1));
        await showcase.GetSlotsAsync(supplier, slug, null, null);
        Assert.Equal(2, probe.PlanCalls);
    }

    [Fact]
    public async Task GetSlotsAsync_ACachedPlan_IsNotShownForAnotherServiceOrAnotherSupplier()
    {
        using var s = await WorldAsync();
        var other = await s.AddOtherSupplierAsync();
        await SetShowcaseSlugAsync(s, other, "altro-srl");
        var probe = new AgendaProbe(s.Agenda);
        var showcase = Showcase(s, probe);
        var second = await s.AddListingAsync("Seconda", durationMinutes: 60);
        var theirs = await s.AddListingAsync("Loro", supplierOrgId: other);

        await showcase.GetSlotsAsync(await SupplierAsync(s), await SlugOfAsync(s, s.ListingId), null, null);
        await showcase.GetSlotsAsync(await SupplierAsync(s), await SlugOfAsync(s, second), null, null);
        await showcase.GetSlotsAsync((await showcase.FindActiveSupplierAsync("altro-srl"))!, await SlugOfAsync(s, theirs), null, null);

        Assert.Equal(3, probe.PlanCalls);
    }

    [Fact]
    public async Task GetSlotsAsync_AServiceWhoseDurationChanged_IsNotServedFromThePlanOfTheOldDuration()
    {
        using var s = await WorldAsync();
        var probe = new AgendaProbe(s.Agenda);
        var showcase = Showcase(s, probe);
        var supplier = await SupplierAsync(s);
        var slug = await SlugOfAsync(s, s.ListingId);
        var twoHours = await showcase.GetSlotsAsync(supplier, slug, Today.AddDays(1), 1);

        await SetDurationAsync(s, s.ListingId, 60);
        var oneHour = await showcase.GetSlotsAsync(supplier, slug, Today.AddDays(1), 1);

        Assert.Equal(2, probe.PlanCalls);
        Assert.Equal(120, twoHours!.DurationMinutes);
        Assert.Equal(60, oneHour!.DurationMinutes);
        Assert.All(oneHour.Days[0].Slots, slot => Assert.Equal(TimeSpan.FromHours(1), slot.EndUtc - slot.StartUtc));
    }

    [Fact]
    public async Task GetSlotsAsync_APlanNeverOutlivesMidnightInRome()
    {
        using var s = await WorldAsync();
        var probe = new AgendaProbe(s.Agenda);
        var showcase = Showcase(s, probe);
        var supplier = await SupplierAsync(s);
        var slug = await SlugOfAsync(s, s.ListingId);
        s.Clock.SetUtcNow(new DateTimeOffset(2026, 10, 8, 21, 59, 50, TimeSpan.Zero)); // 23:59:50 in Rome

        var before = await showcase.GetSlotsAsync(supplier, slug, null, 1);
        s.Clock.Advance(TimeSpan.FromSeconds(15)); // 00:00:05 on the 9th, inside the 30 seconds
        var after = await showcase.GetSlotsAsync(supplier, slug, null, 1);

        Assert.Equal(2, probe.PlanCalls);
        Assert.Equal(Today, before!.Days[0].Date);
        Assert.Equal(Today.AddDays(1), after!.Days[0].Date);
    }

    // ─── The estimate ────────────────────────────────────────────────────────────

    [Fact]
    public async Task QuoteAsync_APublishedService_IsPricedFromTheCatalog()
    {
        using var s = await WorldAsync();
        var slug = await SlugOfAsync(s, s.ListingId);

        var quote = await Showcase(s).QuoteAsync(await SupplierAsync(s), slug, Request());

        Assert.NotNull(quote);
        Assert.Equal(SupplierQuoteOutcome.Estimate, quote.Quote.Outcome);
        Assert.Equal(6000, quote.Quote.TotalCents);
        Assert.Equal(PublicQuoteCoverage.Unknown, quote.Coverage);
        Assert.Equal(ServiceRequestScenario.ServiceName, quote.Service.Name);
    }

    [Fact]
    public async Task QuoteAsync_AServiceThatIsNotPublished_IsNull_AndABlankServiceIsRefusedWithItsField()
    {
        using var s = await WorldAsync();
        var draft = await s.AddListingAsync("Bozza", status: SupplierServiceListingStatus.Draft);
        var showcase = Showcase(s);
        var supplier = await SupplierAsync(s);

        Assert.Null(await showcase.QuoteAsync(supplier, await SlugOfAsync(s, draft), Request()));
        Assert.Null(await showcase.QuoteAsync(supplier, "non-esiste", Request()));
        var blank = await Assert.ThrowsAsync<SupplierQuoteRuleException>(() => showcase.QuoteAsync(supplier, "  ", Request()));
        Assert.Equal(new[] { "service" }, blank.Fields);
        Assert.IsAssignableFrom<DomainRuleException>(blank);
    }

    [Fact]
    public async Task QuoteAsync_AValueThatDoesNotFitTheService_IsRefusedWithTheFields()
    {
        using var s = await WorldAsync();
        var slug = await SlugOfAsync(s, s.ListingId);

        var supplier = await SupplierAsync(s);

        var ex = await Assert.ThrowsAsync<SupplierQuoteRuleException>(() => Showcase(s).QuoteAsync(
            supplier, slug, new SupplierQuoteRequest(5, null, [new SupplierQuoteOption("non-esiste", 1)], null, "12")));

        Assert.Equal(new[] { "quantity", "options[0].code", "postalCode" }, ex.Fields);
    }

    [Fact]
    public async Task QuoteAsync_AComuneTheSupplierCovers_IsCovered_AndOneItDoesNot_IsOutsideWithNoTotal()
    {
        using var s = await WorldAsync();
        var slug = await SlugOfAsync(s, s.ListingId);
        var showcase = Showcase(s);
        var supplier = await SupplierAsync(s);

        var inside = await showcase.QuoteAsync(supplier, slug, Request(comune: "H501", postalCode: "00184"));
        var outside = await showcase.QuoteAsync(supplier, slug, Request(comune: "Milano"));

        Assert.Equal(PublicQuoteCoverage.Covered, inside!.Coverage);
        Assert.Equal("00184", inside.PostalCode);
        Assert.Equal(6000, inside.Quote.TotalCents);
        Assert.Equal(PublicQuoteCoverage.Outside, outside!.Coverage);
        Assert.Equal(SupplierQuoteOutcome.OutsideArea, outside.Quote.Outcome);
        Assert.Null(outside.Quote.TotalCents);
    }

    [Fact]
    public async Task QuoteAsync_ThePostalCode_DoesNotDecideTheCoverage_AndWithNoComuneNothingIsChecked()
    {
        using var s = await WorldAsync();
        var slug = await SlugOfAsync(s, s.ListingId);
        var matcher = new Mock<ISupplierComuneMatcher>(MockBehavior.Strict);
        var showcase = Showcase(s, matcher: matcher.Object);

        var quote = await showcase.QuoteAsync(await SupplierAsync(s), slug, Request(postalCode: "20121"));

        Assert.Equal(PublicQuoteCoverage.Unknown, quote!.Coverage);
        Assert.Equal(SupplierQuoteOutcome.Estimate, quote.Quote.Outcome);
        matcher.VerifyNoOtherCalls(); // a strict mock: any call would have thrown
    }

    [Fact]
    public async Task QuoteAsync_TheComune_IsCheckedAgainstTheSupplierThatWasFound_AsAnIstatCodeOrAName()
    {
        using var s = await WorldAsync();
        var slug = await SlugOfAsync(s, s.ListingId);
        var matcher = new Mock<ISupplierComuneMatcher>();
        matcher.Setup(m => m.CoversAsync(It.IsAny<SupplierProfile>(), It.IsAny<ComuneTarget>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);
        var showcase = Showcase(s, matcher: matcher.Object);

        await showcase.QuoteAsync(await SupplierAsync(s), slug, Request(comune: "058091"));
        await showcase.QuoteAsync(await SupplierAsync(s), slug, Request(comune: " Roma "));

        matcher.Verify(m => m.CoversAsync(It.Is<SupplierProfile>(p => p.OrgId == s.SupplierOrgId), new ComuneTarget("058091", null), It.IsAny<CancellationToken>()), Times.Once);
        matcher.Verify(m => m.CoversAsync(It.Is<SupplierProfile>(p => p.OrgId == s.SupplierOrgId), new ComuneTarget(null, "Roma"), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task QuoteAsync_AServiceOnQuote_IsAnAnswerNotAnError()
    {
        using var s = await WorldAsync();
        var onQuote = await s.AddListingAsync("Su preventivo", priceFromCents: null, requiresQuote: true);

        var quote = await Showcase(s).QuoteAsync(await SupplierAsync(s), await SlugOfAsync(s, onQuote), Request());

        Assert.Equal(SupplierQuoteOutcome.OnQuote, quote!.Quote.Outcome);
        Assert.Equal(SupplierQuoteReason.RequiresQuote, quote.Quote.Reason);
    }

    // ─── Helpers ─────────────────────────────────────────────────────────────────

    private static async Task<ServiceRequestScenario> WorldAsync()
    {
        var s = await ServiceRequestScenario.CreateAsync();
        await SetShowcaseSlugAsync(s, s.SupplierOrgId, Slug);
        return s;
    }

    private static PublicSupplierShowcaseService Showcase(
        ServiceRequestScenario s,
        ISupplierAgendaService? agenda = null,
        ISupplierComuneMatcher? matcher = null)
    {
        // The comuni list is not imported here: the matcher falls back to the names the supplier wrote (H501), as it does in
        // an environment without the list.
        var directory = new Mock<IComuneDirectory>();
        directory
            .Setup(d => d.ResolveAsync(It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<string, Comune>());

        return new PublicSupplierShowcaseService(
            s.Db,
            s.Catalog,
            agenda ?? s.Agenda,
            new SupplierKpiService(s.Db, s.Clock),
            matcher ?? new SupplierComuneMatcher(directory.Object),
            new PublicSupplierSlotCache(s.Clock),
            s.Clock);
    }

    private static async Task<SupplierProfile> SupplierAsync(ServiceRequestScenario s) =>
        (await Showcase(s).FindActiveSupplierAsync(Slug))!;

    private static SupplierQuoteRequest Request(string? comune = null, string? postalCode = null) =>
        new(null, null, null, comune, postalCode);

    private static async Task SetShowcaseSlugAsync(ServiceRequestScenario s, Guid orgId, string slug)
    {
        var profile = await s.Db.SupplierProfiles.SingleAsync(sp => sp.OrgId == orgId);
        profile.ShowcaseSlug = slug;
        await s.Db.SaveChangesAsync();
        s.Db.ChangeTracker.Clear();
    }

    private static async Task<string> SlugOfAsync(ServiceRequestScenario s, Guid listingId) =>
        (await s.Db.SupplierServiceListings.AsNoTracking().SingleAsync(l => l.Id == listingId)).Slug;

    private static async Task SetListingSlugAsync(ServiceRequestScenario s, Guid listingId, string slug)
    {
        var listing = await s.Db.SupplierServiceListings.SingleAsync(l => l.Id == listingId);
        listing.Slug = slug;
        await s.Db.SaveChangesAsync();
        s.Db.ChangeTracker.Clear();
    }

    private static async Task SetSortOrderAsync(ServiceRequestScenario s, Guid listingId, int sortOrder)
    {
        var listing = await s.Db.SupplierServiceListings.SingleAsync(l => l.Id == listingId);
        listing.SortOrder = sortOrder;
        await s.Db.SaveChangesAsync();
        s.Db.ChangeTracker.Clear();
    }

    private static async Task SetDurationAsync(ServiceRequestScenario s, Guid listingId, int minutes)
    {
        var listing = await s.Db.SupplierServiceListings.SingleAsync(l => l.Id == listingId);
        listing.DurationMinutes = minutes;
        await s.Db.SaveChangesAsync();
        s.Db.ChangeTracker.Clear();
    }

    private static void Taken(ServiceRequest request, DateTime createdAt, int minutesToTake)
    {
        request.CreatedAt = createdAt;
        request.UpdatedAt = createdAt;
        request.TakenAt = createdAt.AddMinutes(minutesToTake);
    }

    /// <summary>
    /// The real agenda with a counter on the plans it computes, and extra occupancies (the holds SP-10 will add) put in the
    /// planning input of every plan.
    /// </summary>
    private sealed class AgendaProbe(ISupplierAgendaService inner) : ISupplierAgendaService
    {
        public int PlanCalls { get; private set; }

        public IReadOnlyList<SupplierOccupancy> Extra { get; init; } = [];

        public async Task<IReadOnlyList<SupplierDayPlan>> PlanAsync(
            Guid supplierOrgId, DateOnly from, DateOnly to, SupplierSlotQuery query, CancellationToken cancellationToken = default)
        {
            PlanCalls++;
            var input = await inner.BuildPlanningInputAsync(supplierOrgId, from, to, cancellationToken);
            return SupplierSlotPlanner.PlanRange(from, to, input with { Occupancies = [.. input.Occupancies, .. Extra] }, query);
        }

        public Task<IReadOnlyList<SupplierDayPlan>> PlanAsync(
            Guid supplierOrgId, DateOnly from, DateOnly to, SupplierSlotQuery query, Guid? exceptRequestId, CancellationToken cancellationToken = default) =>
            inner.PlanAsync(supplierOrgId, from, to, query, exceptRequestId, cancellationToken);

        public Task<SupplierPlanningRules> GetRulesAsync(Guid supplierOrgId, CancellationToken cancellationToken = default) =>
            inner.GetRulesAsync(supplierOrgId, cancellationToken);

        public Task<SupplierPlanningInput> BuildPlanningInputAsync(
            Guid supplierOrgId, DateOnly from, DateOnly to, CancellationToken cancellationToken = default) =>
            inner.BuildPlanningInputAsync(supplierOrgId, from, to, cancellationToken);

        public Task<SupplierPlanningInput> BuildPlanningInputAsync(
            Guid supplierOrgId, DateOnly from, DateOnly to, Guid? exceptRequestId, CancellationToken cancellationToken = default) =>
            inner.BuildPlanningInputAsync(supplierOrgId, from, to, exceptRequestId, cancellationToken);

        public Task<SupplierHours> GetHoursAsync(Guid supplierOrgId, CancellationToken cancellationToken = default) =>
            inner.GetHoursAsync(supplierOrgId, cancellationToken);

        public Task<SupplierHours> ReplaceHoursAsync(Guid supplierOrgId, SupplierHoursInput input, CancellationToken cancellationToken = default) =>
            inner.ReplaceHoursAsync(supplierOrgId, input, cancellationToken);

        public Task<IReadOnlyList<SupplierTimeOff>> ListTimeOffAsync(Guid supplierOrgId, CancellationToken cancellationToken = default) =>
            inner.ListTimeOffAsync(supplierOrgId, cancellationToken);

        public Task<SupplierTimeOff> AddTimeOffAsync(Guid supplierOrgId, SupplierTimeOffInput input, CancellationToken cancellationToken = default) =>
            inner.AddTimeOffAsync(supplierOrgId, input, cancellationToken);

        public Task DeleteTimeOffAsync(Guid supplierOrgId, Guid id, CancellationToken cancellationToken = default) =>
            inner.DeleteTimeOffAsync(supplierOrgId, id, cancellationToken);

        public Task<IReadOnlyList<SupplierBusyWindow>> ListBlocksAsync(Guid supplierOrgId, CancellationToken cancellationToken = default) =>
            inner.ListBlocksAsync(supplierOrgId, cancellationToken);

        public Task<SupplierBusyWindow> AddBlockAsync(Guid supplierOrgId, SupplierBlockInput input, CancellationToken cancellationToken = default) =>
            inner.AddBlockAsync(supplierOrgId, input, cancellationToken);

        public Task DeleteBlockAsync(Guid supplierOrgId, Guid id, CancellationToken cancellationToken = default) =>
            inner.DeleteBlockAsync(supplierOrgId, id, cancellationToken);

        public Task<SupplierPlanningRules> ReplaceRulesAsync(Guid supplierOrgId, SupplierRulesInput input, CancellationToken cancellationToken = default) =>
            inner.ReplaceRulesAsync(supplierOrgId, input, cancellationToken);

        public Task<SupplierCalendar> GetCalendarAsync(Guid supplierOrgId, DateOnly from, DateOnly to, CancellationToken cancellationToken = default) =>
            inner.GetCalendarAsync(supplierOrgId, from, to, cancellationToken);
    }
}
