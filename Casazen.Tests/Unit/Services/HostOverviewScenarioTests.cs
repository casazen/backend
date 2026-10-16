using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Services;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// The data of <see cref="HostOverviewScenario"/> says what the PostgreSQL tests (<c>HostOverviewPostgresTests</c>) rely on: every
/// chip, link state, channel and intervention is there, the properties of the other org have them too and none of it is seen, and
/// the soonest arrivals of the org are not tied at the edge of the list. Proved here on the InMemory reference, which runs everywhere.
/// </summary>
public class HostOverviewScenarioTests
{
    private static async Task<(AppDbContext Db, HostScopeWorld World)> SeedAsync()
    {
        var db = HostScopeScenario.NewInMemoryDb();
        var world = await HostScopeScenario.SeedAsync(db);
        await HostOverviewScenario.AddAsync(db, world);
        return (db, world);
    }

    private static BookingSearchService Search(AppDbContext db) => new(db, new ConfigurationBuilder().Build(), HostOverviewScenario.Clock);

    [Fact]
    public async Task Counts_EveryChipHasBookings_TheOtherOrgsAreNotCounted_AndTheChipsShareTheBookingsOut()
    {
        var (db, world) = await SeedAsync();
        await using var _ = db;
        // What PostgreSQL would refuse: a key that points at nothing, a unique index that holds the same key twice.
        await HostScopeScenario.AssertReferentialIntegrityAsync(db);

        var wholeOrg = await Search(db).CountAsync(world.OrgWide, new BookingSearchCriteria());
        var restricted = await Search(db).CountAsync(world.Restricted, new BookingSearchCriteria());

        // Nine bookings for each property: the three of the scenario and six of this one. The first property has its arrival today (in
        // progress), the second one's is on the 11th (to come).
        Assert.Equal(new BookingCounts(18, 2, 5, 5, 4, 2), wholeOrg);
        Assert.Equal(new BookingCounts(9, 1, 2, 3, 2, 1), restricted);
        Assert.Equal(wholeOrg.All, wholeOrg.ToConfirm + wholeOrg.Upcoming + wholeOrg.InProgress + wholeOrg.Completed + wholeOrg.Cancelled);
    }

    [Fact]
    public async Task Channels_And_LinkStates_AreAllThere()
    {
        var (db, world) = await SeedAsync();
        await using var _ = db;

        async Task<int> CountAsync(BookingSearchCriteria criteria) => (await Search(db).CountAsync(world.OrgWide, criteria)).All;

        Assert.Equal(
            new[] { 2, 4, 6, 4, 2 },
            new[]
            {
                await CountAsync(new BookingSearchCriteria(Channels: [BookingChannel.Airbnb])),
                await CountAsync(new BookingSearchCriteria(Channels: [BookingChannel.Direct])),
                await CountAsync(new BookingSearchCriteria(Channels: [BookingChannel.Manual])),
                await CountAsync(new BookingSearchCriteria(Channels: [BookingChannel.Other])),
                await CountAsync(new BookingSearchCriteria(Channels: [BookingChannel.BookingCom])),
            });
        Assert.Equal(
            new[] { 10, 2, 2, 2, 2 },
            new[]
            {
                await CountAsync(new BookingSearchCriteria(CheckInStates: [HostTodayCheckInState.NotSent])),
                await CountAsync(new BookingSearchCriteria(CheckInStates: [HostTodayCheckInState.Sent])),
                await CountAsync(new BookingSearchCriteria(CheckInStates: [HostTodayCheckInState.InProgress])),
                await CountAsync(new BookingSearchCriteria(CheckInStates: [HostTodayCheckInState.Completed])),
                await CountAsync(new BookingSearchCriteria(CheckInStates: [HostTodayCheckInState.Expired])),
            });
    }

    [Fact]
    public async Task Today_HasTheStaysTheInterventionsAndTheThingsToDoTheComparisonsLookAt()
    {
        var (db, world) = await SeedAsync();
        await using var _ = db;
        var service = HostTodayServiceTests.NewService(db);

        var wholeOrg = await service.GetTodayAsync(world.OrgWide, new HostTodayOptions(IncludePayments: true));
        var restricted = await service.GetTodayAsync(world.Restricted, new HostTodayOptions(IncludePayments: true));

        // Arrivals, departures, check-ins to come, interventions of the day and requests waiting: only the first property arrives today.
        Assert.Equal((1, 2, 5, 4, 2), (wholeOrg.Arrivals.Count, wholeOrg.Departures.Count, wholeOrg.Upcoming.Count, wholeOrg.Services.Count, wholeOrg.Approvals.Count));
        Assert.Equal((1, 1, 2, 2, 1), (restricted.Arrivals.Count, restricted.Departures.Count, restricted.Upcoming.Count, restricted.Services.Count, restricted.Approvals.Count));
        var arrival = Assert.Single(restricted.Arrivals.Items);
        Assert.Equal(("arrives", AlloggiatiWebStatus.DaInviareManualmente, HostTodayCheckInState.Sent), (arrival.Stay.GuestName.Split(' ')[0], arrival.Alloggiati!.Status, arrival.CheckIn!.State));
        var departure = Assert.Single(restricted.Departures.Items);
        Assert.Equal(ServiceRequestStatus.Richiesto, departure.Cleaning!.Status);
        // The things to do about an intervention, the three kinds, in the scope of the caller. The org's list is cut at twenty, after the
        // fifth priority, so the jobs to pay (the sixth) are the collaborator's list alone.
        Assert.Equal(14, restricted.Todo.Count);
        Assert.Equal(
            new[] { HostTodoAction.RespondToServiceRequest, HostTodoAction.ConfirmFinalAmount, HostTodoAction.PayServiceRequest },
            restricted.Todo.Items.Where(i => i.ServiceRequestId is not null).Select(i => i.Action).Order());
        Assert.Equal(20, wholeOrg.Todo.Items.Count);
        Assert.Equal(26, wholeOrg.Todo.Count);
        Assert.Equal(
            new[] { HostTodoAction.RespondToServiceRequest, HostTodoAction.RespondToServiceRequest, HostTodoAction.ConfirmFinalAmount, HostTodoAction.ConfirmFinalAmount },
            wholeOrg.Todo.Items.Where(i => i.ServiceRequestId is not null).Select(i => i.Action));
        Assert.DoesNotContain(wholeOrg.Services.Items, s => s.PropertyId == world.OtherOrgProperty.Id);
    }

    [Fact]
    public async Task Overview_HasMoneyCashPendingRequestsAndTheArrivalsThatAreNotTied()
    {
        var (db, world) = await SeedAsync();
        await using var _ = db;
        var dashboard = new HostDashboardService(db, new ConfigurationBuilder().Build(), HostOverviewScenario.Clock);

        var months = await dashboard.GetOverviewAsync(
            world.OrgWide, new HostOverviewQuery(HostDashboardPeriodKind.Custom, From: new DateOnly(2026, 10, 1), To: new DateOnly(2026, 12, 31), IncludeMoney: true));
        var october = await dashboard.GetOverviewAsync(
            world.OrgWide, new HostOverviewQuery(HostDashboardPeriodKind.Month, new DateOnly(2026, 10, 1), IncludeMoney: true));
        var next = await dashboard.GetOverviewAsync(world.OrgWide, new HostOverviewQuery(HostDashboardPeriodKind.Next30Days, IncludeMoney: true));
        var restricted = await dashboard.GetOverviewAsync(world.Restricted, new HostOverviewQuery(HostDashboardPeriodKind.Next30Days, IncludeMoney: true));

        Assert.Equal((2, 1), (next.PropertyCount, restricted.PropertyCount));
        Assert.Equal(HostOverviewSeriesUnit.Month, months.Series!.Unit);
        Assert.Equal(["2026-10", "2026-11", "2026-12"], months.Series.Buckets.Select(b => b.Label));
        Assert.Equal(months.Revenue!.Amount, months.Series.Buckets.Sum(b => b.Amount));
        // The two requests of November are waiting for the host.
        Assert.Equal(2, months.Upcoming.PendingApproval);
        Assert.True(october.Collected!.PaymentCount > 0 && october.Revenue!.Amount > 0);
        // The soonest three of the org: today's arrival, then the two stays of the 10th. The fourth (the 11th) is not tied with them.
        var arrivals = next.NextArrivals.Select(s => (s.PropertyName, s.GuestName.Split(' ')[0], s.CheckInDate.Day)).Order().ToList();
        Assert.Equal(
            [("Casa Bianca", "Anna", 10), ("Trullo", "Anna", 10), ("Trullo", "arrives", 9)],
            arrivals);
        Assert.All(restricted.NextArrivals, s => Assert.Equal(world.Granted.Id, s.PropertyId));
        Assert.Equal(3, restricted.NextArrivals.Count);
    }
}
