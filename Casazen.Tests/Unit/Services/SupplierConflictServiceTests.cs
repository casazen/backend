using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Suppliers;
using Casazen.Infrastructure.Services;
using Xunit;

namespace Casazen.Tests.Unit.Services;

public class SupplierConflictServiceTests
{
    private static readonly DateOnly From = new(2026, 10, 8);
    private static readonly DateOnly To = new(2026, 11, 7);
    private static readonly DateOnly Friday = new(2026, 10, 9);

    private static SupplierConflictService Service(ServiceRequestScenario s) => new(s.Agenda, s.Reader, s.Catalog);

    [Fact]
    public async Task GetConflicts_ARequestWaitingOverAJobTaken_IsAnOverlapOfTheWaitingOne_WithTheFirstFreeSlotAsTheSuggestion()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var taken = await s.TakenAsync(ServiceRequestScenario.FridayAt10);
        var waiting = await s.SeedAsync(ServiceRequestStatus.Richiesto, request =>
        {
            request.ScheduledStartUtc = ServiceRequestScenario.FridayAt10.AddHours(1);
            request.ScheduledEndUtc = ServiceRequestScenario.FridayAt10.AddHours(3);
            request.ServiceListingId = s.ListingId;
            request.ServiceNameSnapshot = ServiceRequestScenario.ServiceName;
        });

        var report = await Service(s).GetConflictsAsync(s.SupplierOrgId, From, To);

        var item = Assert.Single(report.Items);
        Assert.Equal(waiting.Id, item.RequestId);
        Assert.Equal(ServiceRequestStatus.Richiesto, item.Status);
        Assert.Equal(Friday, item.Date);
        var conflict = Assert.Single(item.Conflicts);
        Assert.Equal(SupplierConflictCodes.Overlap, conflict.Code);
        Assert.Equal(SupplierConflictCodes.Danger, conflict.Severity);
        Assert.Equal(taken.Id, conflict.OtherRequestId);
        Assert.False(report.Truncated);
        Assert.Equal((From, To), (report.From, report.To));

        var expected = (await s.Agenda.PlanAsync(
                s.SupplierOrgId, Friday, Friday.AddDays(21), new SupplierSlotQuery(120, null, SupplierServiceWeekdays.AllMask), waiting.Id))
            .SelectMany(plan => plan.Slots)
            .First();
        Assert.Equal(expected, item.SuggestedSlot);
        Assert.Equal(ServiceRequestScenario.FridayAt14, item.SuggestedSlot!.Value.StartUtc);
        Assert.Equal(TimeSpan.FromMinutes(120), item.SuggestedSlot.Value.EndUtc - item.SuggestedSlot.Value.StartUtc);
    }

    [Fact]
    public async Task GetConflicts_AJobAlreadyTaken_HasItsConflictsButNoSuggestion()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var taken = await Seed(s, ServiceRequestStatus.PresoInCarico, ServiceRequestScenario.SundayAt10);

        var item = Assert.Single((await Service(s).GetConflictsAsync(s.SupplierOrgId, From, To)).Items);

        Assert.Equal(taken.Id, item.RequestId);
        Assert.Equal([SupplierConflictCodes.DayOff], item.Conflicts.Select(conflict => conflict.Code));
        Assert.Null(item.SuggestedSlot);
    }

    [Fact]
    public async Task GetConflicts_TheSuggestionIsLookedForInTheDaysAfterTheRange_NotOnlyInsideIt()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var waiting = await Seed(s, ServiceRequestStatus.Richiesto, ServiceRequestScenario.FridayAt10);
        await s.Agenda.AddBlockAsync(s.SupplierOrgId, new SupplierBlockInput(
            SupplierBusyWindowKind.Block, ServiceRequestScenario.FridayAt10.AddHours(-2), ServiceRequestScenario.FridayAt10.AddHours(10), "Chiuso"));

        var item = Assert.Single((await Service(s).GetConflictsAsync(s.SupplierOrgId, Friday, Friday)).Items);

        Assert.Equal(waiting.Id, item.RequestId);
        Assert.Equal(SupplierConflictCodes.Block, item.Conflicts[0].Code);
        Assert.Equal(new DateTime(2026, 10, 10, 6, 0, 0, DateTimeKind.Utc), item.SuggestedSlot!.Value.StartUtc);
    }

    [Fact]
    public async Task GetConflicts_TheWeekdaysOfTheServiceOfTheRequest_DecideTheSuggestion()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var onlyTuesday = await s.AddListingAsync("Solo di martedì", weekdaysMask: SupplierServiceWeekdays.ToMask([DayOfWeek.Tuesday]));
        var forTuesdays = await Seed(s, ServiceRequestStatus.Richiesto, ServiceRequestScenario.SundayAt10, listingId: onlyTuesday);
        var forAnyDay = await Seed(s, ServiceRequestStatus.Richiesto, ServiceRequestScenario.SundayAt10.AddHours(3), listingId: s.ListingId);

        var items = (await Service(s).GetConflictsAsync(s.SupplierOrgId, From, To)).Items;

        Assert.Equal(
            new DateTime(2026, 10, 13, 6, 0, 0, DateTimeKind.Utc),
            items.Single(item => item.RequestId == forTuesdays.Id).SuggestedSlot!.Value.StartUtc);
        Assert.Equal(
            new DateTime(2026, 10, 12, 6, 0, 0, DateTimeKind.Utc),
            items.Single(item => item.RequestId == forAnyDay.Id).SuggestedSlot!.Value.StartUtc);
    }

    [Fact]
    public async Task GetConflicts_TheNoticeOfTheServiceOfTheRequest_DecidesTheEarliestStartOfTheSuggestion()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var slow = await s.AddListingAsync("Con preavviso", minNoticeHours: 5 * 24);
        var waiting = await Seed(s, ServiceRequestStatus.Richiesto, ServiceRequestScenario.SundayAt10, listingId: slow);

        var item = Assert.Single((await Service(s).GetConflictsAsync(s.SupplierOrgId, From, To)).Items);

        Assert.Equal(waiting.Id, item.RequestId);
        Assert.Equal(new DateTime(2026, 10, 13, 12, 0, 0, DateTimeKind.Utc), item.SuggestedSlot!.Value.StartUtc);
    }

    [Fact]
    public async Task GetConflicts_AJobThatHasStarted_IsNotLookedAt_EvenIfItWouldHaveAConflict()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var morning = new DateTime(2026, 10, 8, 6, 0, 0, DateTimeKind.Utc);
        await Seed(s, ServiceRequestStatus.InCorso, morning, minutes: 300);
        await s.Agenda.AddBlockAsync(
            s.SupplierOrgId,
            new SupplierBlockInput(SupplierBusyWindowKind.Block, morning.AddMinutes(270), morning.AddHours(6), "Blocco"));

        var report = await Service(s).GetConflictsAsync(s.SupplierOrgId, From, To);

        Assert.Empty(report.Items);
    }

    [Theory]
    [InlineData(ServiceRequestStatus.Rifiutato)]
    [InlineData(ServiceRequestStatus.Annullato)]
    [InlineData(ServiceRequestStatus.Completato)]
    [InlineData(ServiceRequestStatus.Pagato)]
    public async Task GetConflicts_ARequestThatIsNotAJob_IsNotLookedAt(ServiceRequestStatus status)
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        await Seed(s, status, ServiceRequestScenario.SundayAt10);

        Assert.Empty((await Service(s).GetConflictsAsync(s.SupplierOrgId, From, To)).Items);
    }

    [Fact]
    public async Task GetConflicts_TheRequestsOfAnotherSupplier_NeverShowUp_AndNeverMakeAConflict()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var other = await s.AddOtherSupplierAsync();
        await Seed(s, ServiceRequestStatus.PresoInCarico, ServiceRequestScenario.SundayAt10, supplierOrgId: other);
        // And a job of the other supplier at the same time as a request of mine is not in the way of it.
        await Seed(s, ServiceRequestStatus.PresoInCarico, ServiceRequestScenario.FridayAt10, supplierOrgId: other);
        var mine = await Seed(s, ServiceRequestStatus.Richiesto, ServiceRequestScenario.FridayAt10);

        var mineReport = await Service(s).GetConflictsAsync(s.SupplierOrgId, From, To);
        var theirs = await Service(s).GetConflictsAsync(other, From, To);

        Assert.Empty(mineReport.Items);
        Assert.NotEmpty(theirs.Items);
        Assert.DoesNotContain(theirs.Items, item => item.RequestId == mine.Id);
    }

    [Fact]
    public async Task GetConflicts_ASupplierWithNothingToCheck_GetsAnEmptyAnswer()
    {
        using var s = await ServiceRequestScenario.CreateAsync();

        var report = await Service(s).GetConflictsAsync(s.SupplierOrgId, From, To);

        Assert.Empty(report.Items);
        Assert.False(report.Truncated);
    }

    [Fact]
    public async Task GetConflicts_MoreJobsThanTheLimit_ChecksTheNearestAndSaysSo()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var sundays = new[] { new DateTime(2026, 10, 11), new DateTime(2026, 10, 18), new DateTime(2026, 10, 25), new DateTime(2026, 11, 1) };
        var extra = 5;
        var total = SupplierAgendaLimits.MaxConflictRequests + extra;
        for (var i = 0; i < total; i++)
        {
            var start = DateTime.SpecifyKind(sundays[i % sundays.Length], DateTimeKind.Utc).AddHours(6).AddMinutes(10 * (i / sundays.Length));
            await Seed(s, ServiceRequestStatus.PresoInCarico, start, minutes: 10);
        }

        var report = await Service(s).GetConflictsAsync(s.SupplierOrgId, From, To);

        Assert.True(report.Truncated);
        Assert.Equal(SupplierAgendaLimits.MaxConflictRequests, report.Items.Count);
        Assert.Equal(new DateTime(2026, 10, 11, 6, 0, 0, DateTimeKind.Utc), report.Items[0].StartUtc);
        Assert.Equal(report.Items.OrderBy(item => item.StartUtc).Select(item => item.RequestId), report.Items.Select(item => item.RequestId));
    }

    [Fact]
    public async Task GetConflicts_ARangeThatIsReversedOrLongerThan62Days_IsRefused()
    {
        using var s = await ServiceRequestScenario.CreateAsync();

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => Service(s).GetConflictsAsync(s.SupplierOrgId, To, From));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => Service(s).GetConflictsAsync(s.SupplierOrgId, From, From.AddDays(62)));
        Assert.NotNull(await Service(s).GetConflictsAsync(s.SupplierOrgId, From, From.AddDays(61)));
    }

    [Fact]
    public async Task GetConflicts_ARangeAtAnEndOfTheCalendar_IsRefused_TheFarthestRangesThatPassAreAnswered()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var first = DateOnly.MinValue.AddDays(SupplierAgendaLimits.RangeEdgeDays);
        var last = DateOnly.MaxValue.AddDays(-SupplierAgendaLimits.RangeEdgeDays - SupplierAgendaLimits.ConflictSuggestionDays);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => Service(s).GetConflictsAsync(s.SupplierOrgId, DateOnly.MinValue, DateOnly.MinValue.AddDays(30)));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => Service(s).GetConflictsAsync(s.SupplierOrgId, DateOnly.MaxValue.AddDays(-30), DateOnly.MaxValue));

        Assert.NotNull(await Service(s).GetConflictsAsync(s.SupplierOrgId, last.AddDays(-30), last));
        Assert.NotNull(await Service(s).GetConflictsAsync(s.SupplierOrgId, first, first.AddDays(30)));
    }

    [Fact]
    public async Task GetConflicts_ADayClosedByTheCalendarFeed_IsExternal_AndOneClosedByHand_IsDayOff()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var monday = new DateOnly(2026, 10, 12);
        var tuesday = new DateOnly(2026, 10, 13);
        s.Db.SupplierAvailability.AddRange(
            new SupplierAvailability { OrgId = s.SupplierOrgId, Date = monday, Available = false, Source = SupplierAvailabilitySource.ICalFeed },
            new SupplierAvailability { OrgId = s.SupplierOrgId, Date = tuesday, Available = false, Source = SupplierAvailabilitySource.Manual });
        await s.Db.SaveChangesAsync();
        var onMonday = await Seed(s, ServiceRequestStatus.Richiesto, new DateTime(2026, 10, 12, 8, 0, 0, DateTimeKind.Utc));
        var onTuesday = await Seed(s, ServiceRequestStatus.Richiesto, new DateTime(2026, 10, 13, 8, 0, 0, DateTimeKind.Utc));

        var items = (await Service(s).GetConflictsAsync(s.SupplierOrgId, From, To)).Items;

        // What the feed closed is "another engagement" (external, danger), what the supplier closed is "a day off" (warning).
        Assert.Equal([SupplierConflictCodes.External], items.Single(item => item.RequestId == onMonday.Id).Conflicts.Select(conflict => conflict.Code));
        Assert.Equal([SupplierConflictCodes.DayOff], items.Single(item => item.RequestId == onTuesday.Id).Conflicts.Select(conflict => conflict.Code));
    }

    [Fact]
    public async Task GetConflicts_AnEngagementOfTheFeedAndABlockByHand_AreNamedByTheirOwnWindows()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var engagement = new SupplierBusyWindow
        {
            OrgId = s.SupplierOrgId,
            StartUtc = ServiceRequestScenario.FridayAt10.AddHours(1),
            EndUtc = ServiceRequestScenario.FridayAt10.AddHours(2),
            Kind = SupplierBusyWindowKind.External,
            Source = SupplierBusyWindowSource.ICalFeed,
            Label = "Dentista",
            ExternalUid = "uid-1",
        };
        s.Db.SupplierBusyWindows.Add(engagement);
        await s.Db.SaveChangesAsync();
        var block = await s.Agenda.AddBlockAsync(s.SupplierOrgId, new SupplierBlockInput(
            SupplierBusyWindowKind.Block, ServiceRequestScenario.FridayAt14.AddMinutes(30), ServiceRequestScenario.FridayAt14.AddMinutes(90), "Chiuso"));
        var overTheEngagement = await Seed(s, ServiceRequestStatus.Richiesto, ServiceRequestScenario.FridayAt10);
        var overTheBlock = await Seed(s, ServiceRequestStatus.Richiesto, ServiceRequestScenario.FridayAt14);

        var items = (await Service(s).GetConflictsAsync(s.SupplierOrgId, From, To)).Items;

        var external = Assert.Single(items.Single(item => item.RequestId == overTheEngagement.Id).Conflicts);
        Assert.Equal(SupplierConflictCodes.External, external.Code);
        Assert.Equal(engagement.Id, external.WindowId);
        Assert.Null(external.OtherRequestId);
        var blocked = Assert.Single(items.Single(item => item.RequestId == overTheBlock.Id).Conflicts);
        Assert.Equal(SupplierConflictCodes.Block, blocked.Code);
        Assert.Equal(block.Id, blocked.WindowId);
        Assert.Null(blocked.OtherRequestId);
    }

    private static Task<ServiceRequest> Seed(
        ServiceRequestScenario s,
        ServiceRequestStatus status,
        DateTime start,
        int minutes = ServiceRequestScenario.ServiceMinutes,
        Guid? listingId = null,
        Guid? supplierOrgId = null) =>
        s.SeedAsync(
            status,
            request =>
            {
                request.ScheduledStartUtc = start;
                request.ScheduledEndUtc = start.AddMinutes(minutes);
                if (listingId is { } id)
                {
                    request.ServiceListingId = id;
                    request.ServiceNameSnapshot = "Servizio";
                }
            },
            supplierOrgId);
}
