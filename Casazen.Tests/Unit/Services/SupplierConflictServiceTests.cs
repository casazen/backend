using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Suppliers;
using Casazen.Infrastructure.Services;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// SP-03b: <c>GET api/supplier/calendar/conflicts</c> on the real services and an in-memory database: the jobs of the supplier read
/// from its agenda, the planner's own input, the suggestion computed as <c>PlanAsync(exceptRequestId)</c> computes it (with the notice
/// and the weekdays of the request's service), and that nothing of another supplier, nothing that is not a job and nothing already
/// started is looked at. The rules themselves are in <c>SupplierConflictDetectorTests</c>. Today is Thursday 8 October 2026, 12:00 in
/// Rome; the supplier works Monday to Friday 08-13 and 14-18 and Saturday 08-14, with a pause of 30 minutes, 3 jobs a day, no notice.
/// </summary>
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
        var taken = await s.TakenAsync(ServiceRequestScenario.FridayAt10); // Friday 10:00-12:00, taken
        var waiting = await s.SeedAsync(ServiceRequestStatus.Richiesto, request =>
        {
            request.ScheduledStartUtc = ServiceRequestScenario.FridayAt10.AddHours(1); // 11:00-13:00
            request.ScheduledEndUtc = ServiceRequestScenario.FridayAt10.AddHours(3);
            request.ServiceListingId = s.ListingId;
            request.ServiceNameSnapshot = ServiceRequestScenario.ServiceName;
        });

        var report = await Service(s).GetConflictsAsync(s.SupplierOrgId, From, To);

        // Only the waiting request is in trouble: what waits never counts against a job already taken.
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

        // The suggestion is what the planner offers for this request moved: PlanAsync(exceptRequestId), the same duration, the same
        // service. The morning is taken (10:00-12:00 and the pause), the afternoon opens at 14:00.
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
        var taken = await Seed(s, ServiceRequestStatus.PresoInCarico, ServiceRequestScenario.SundayAt10); // the supplier does not work on Sundays

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
        // The whole of Friday is blocked by hand: the request is in the way of the block, and the first free place is on Saturday.
        await s.Agenda.AddBlockAsync(s.SupplierOrgId, new SupplierBlockInput(
            SupplierBusyWindowKind.Block, ServiceRequestScenario.FridayAt10.AddHours(-2), ServiceRequestScenario.FridayAt10.AddHours(10), "Chiuso"));

        var item = Assert.Single((await Service(s).GetConflictsAsync(s.SupplierOrgId, Friday, Friday)).Items); // the range is Friday only

        Assert.Equal(waiting.Id, item.RequestId);
        Assert.Equal(SupplierConflictCodes.Block, item.Conflicts[0].Code);
        Assert.Equal(new DateTime(2026, 10, 10, 6, 0, 0, DateTimeKind.Utc), item.SuggestedSlot!.Value.StartUtc); // Saturday 08:00 in Rome
    }

    [Fact]
    public async Task GetConflicts_TheWeekdaysOfTheServiceOfTheRequest_DecideTheSuggestion()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        var onlyTuesday = await s.AddListingAsync("Solo di martedì", weekdaysMask: SupplierServiceWeekdays.ToMask([DayOfWeek.Tuesday]));
        var forTuesdays = await Seed(s, ServiceRequestStatus.Richiesto, ServiceRequestScenario.SundayAt10, listingId: onlyTuesday);
        var forAnyDay = await Seed(s, ServiceRequestStatus.Richiesto, ServiceRequestScenario.SundayAt10.AddHours(3), listingId: s.ListingId);

        var items = (await Service(s).GetConflictsAsync(s.SupplierOrgId, From, To)).Items;

        // Both are on a Sunday, a day off. The first free place for the service of every day is Monday 08:00 (06:00Z), for the one
        // offered on Tuesdays only it is Tuesday 08:00.
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
        // Five days of notice: from Thursday 12:00 that is Tuesday 13 October 12:00.
        var slow = await s.AddListingAsync("Con preavviso", minNoticeHours: 5 * 24);
        var waiting = await Seed(s, ServiceRequestStatus.Richiesto, ServiceRequestScenario.SundayAt10, listingId: slow);

        var item = Assert.Single((await Service(s).GetConflictsAsync(s.SupplierOrgId, From, To)).Items);

        Assert.Equal(waiting.Id, item.RequestId);
        // Monday is inside the notice and Tuesday's morning band ends at 13:00, before a two-hour slot could start at 12:00: 14:00.
        Assert.Equal(new DateTime(2026, 10, 13, 12, 0, 0, DateTimeKind.Utc), item.SuggestedSlot!.Value.StartUtc);
    }

    [Fact]
    public async Task GetConflicts_AJobThatHasStarted_IsNotLookedAt_EvenIfItWouldHaveAConflict()
    {
        using var s = await ServiceRequestScenario.CreateAsync();
        // 08:00 in Rome today, five hours long: it started four hours ago and is still running while a block starts at 12:30.
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
        Assert.NotEmpty(theirs.Items); // a Sunday is a day off for them too, nothing of mine in it
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
            // Jobs of ten minutes, one after the other, on the four Sundays of the range: all of them a day off.
            var start = DateTime.SpecifyKind(sundays[i % sundays.Length], DateTimeKind.Utc).AddHours(6).AddMinutes(10 * (i / sundays.Length));
            await Seed(s, ServiceRequestStatus.PresoInCarico, start, minutes: 10);
        }

        var report = await Service(s).GetConflictsAsync(s.SupplierOrgId, From, To);

        Assert.True(report.Truncated);
        Assert.Equal(SupplierAgendaLimits.MaxConflictRequests, report.Items.Count);
        // The nearest ones: the first of the first Sunday is there, and so is the last one kept (the furthest are cut).
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

    // ─── helpers ─────────────────────────────────────────────────────────────────

    /// <summary>A request of the supplier written straight to the database, with hours, in <paramref name="status"/>.</summary>
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
