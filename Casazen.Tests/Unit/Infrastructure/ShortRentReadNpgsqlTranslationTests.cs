using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Core.Enums;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Services;
using Casazen.Tests.Unit.Services;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Casazen.Tests.Unit.Infrastructure;

/// <summary>
/// SR-03: every read of the short rent API, run on the PostgreSQL provider with all three kinds of scope, without a server (see
/// <see cref="NpgsqlTranslationProbe"/>). InMemory evaluates a query the way .NET does and accepts what Npgsql cannot turn into
/// SQL (a text search, a correlated subquery in a projection, a date comparison); this fails here, before CI, on a query that has
/// no translation.
/// </summary>
public class ShortRentReadNpgsqlTranslationTests
{
    private static readonly Guid OrgId = Guid.Parse("7a3c1d52-9f0e-4b8a-8d65-2c4e1f0b9a11");
    private static readonly TimeProvider Clock = new Casazen.Tests.Unit.FixedTimeProvider(HostScopeScenario.Now);

    private static HostScope ScopeOf(string kind) => kind switch
    {
        "collaborator" => new HostScope(OrgId, GrantedToUserId: "auth0|collaboratore"),
        "account-in-no-team" => new HostScope(OrgId, OwnerId: "auth0|titolare"),
        _ => new HostScope(OrgId),
    };

    private static HostDashboardService Dashboard(AppDbContext db) => new(db, new ConfigurationBuilder().Build(), Clock);

    private static ComplianceSummaryResult SummaryWithItems()
    {
        var empty = new ComplianceSummarySection(0, []);
        return new ComplianceSummaryResult(
            new ComplianceSummarySection(
                1, [ComplianceSummaryItem.ForProperty(ComplianceCockpitAction.ActivateProperty, Guid.NewGuid(), "Trullo")]),
            new ComplianceSummarySection(
                1, [ComplianceSummaryItem.ForBooking(ComplianceCockpitAction.CompleteGuestCheckIn, Guid.NewGuid(), "Anna Verdi")]),
            empty,
            empty,
            empty,
            empty);
    }

    private static readonly BookingSearchCriteria EveryCriterion = new(
        From: new DateOnly(2026, 10, 1),
        To: new DateOnly(2026, 10, 31),
        Statuses: [BookingStatus.Confirmed, BookingStatus.Pending],
        Query: "anna ABCDE-FGHJK",
        PropertyId: Guid.NewGuid(),
        GuestId: Guid.NewGuid(),
        Page: 1,
        PageSize: 20);

    private static BookingSearchService Search(AppDbContext db) => new(db, new ConfigurationBuilder().Build(), Clock);

    // SR-03b: every new filter at once, with a chip, an order and the paid amount.
    private static readonly BookingSearchCriteria EveryNewCriterion = EveryCriterion with
    {
        ArrivalFrom = new DateOnly(2026, 10, 9),
        ArrivalTo = new DateOnly(2026, 10, 16),
        View = BookingListView.Upcoming,
        Channels = [BookingChannel.Airbnb, BookingChannel.Other],
        CheckInStates = [HostTodayCheckInState.NotSent, HostTodayCheckInState.Completed],
        Sort = new BookingSort(BookingSortKey.Guest, true),
        IncludePaidAmount = true,
    };

    /// <summary>Every call, by the name the failure reports.</summary>
    private static readonly Dictionary<string, Func<AppDbContext, HostScope, Task>> Calls = new()
    {
        ["kpis-month"] = (db, scope) => Dashboard(db).GetKpisAsync(
            scope, new HostDashboardQuery(HostDashboardPeriodKind.Month, new DateOnly(2026, 10, 1), null, Compare: true, IncludeCollected: true)),
        ["kpis-last-30-days"] = (db, scope) => Dashboard(db).GetKpisAsync(
            scope, new HostDashboardQuery(HostDashboardPeriodKind.Last30Days, Compare: true, IncludeCollected: true)),
        ["kpis-next-30-days"] = (db, scope) => Dashboard(db).GetKpisAsync(
            scope, new HostDashboardQuery(HostDashboardPeriodKind.Next30Days, Compare: true, IncludeCollected: true)),
        ["kpis-without-money"] = (db, scope) => Dashboard(db).GetKpisAsync(scope, new HostDashboardQuery(HostDashboardPeriodKind.Next30Days)),
        ["kpis-one-property"] = (db, scope) => Dashboard(db).GetKpisAsync(
            scope, new HostDashboardQuery(HostDashboardPeriodKind.Next30Days, null, Guid.NewGuid(), Compare: true, IncludeCollected: true)),
        ["today-stays"] = (db, scope) => Dashboard(db).GetTodayStaysAsync(scope),
        ["today"] = (db, scope) => HostTodayServiceTests.NewService(db).GetTodayAsync(scope, new HostTodayOptions(IncludePayments: true)),
        ["today-without-payments"] = (db, scope) => HostTodayServiceTests.NewService(db).GetTodayAsync(scope, new HostTodayOptions()),
        ["cockpit-missing"] = (db, scope) => ComplianceMissingServiceTests.Service(db).DescribeAsync(SummaryWithItems()),
        ["bookings-search-everything"] = (db, scope) => new BookingSearchService(db).SearchAsync(scope, EveryCriterion),
        ["bookings-search-plain-text"] = (db, scope) =>
            new BookingSearchService(db).SearchAsync(scope, new BookingSearchCriteria(Query: "verdi")),
        ["bookings-search-page-3"] = (db, scope) =>
            new BookingSearchService(db).SearchAsync(scope, new BookingSearchCriteria(Page: 3, PageSize: 10)),
        ["payments-list-everything"] = (db, scope) => new PaymentListService(db).ListAsync(
            scope,
            new PaymentListCriteria(Guid.NewGuid(), Guid.NewGuid(), new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 31))),
        ["payments-list-plain"] = (db, scope) => new PaymentListService(db).ListAsync(scope, new PaymentListCriteria()),
        // SR-03b: the numbers of the Home. The properties are none here (the probe answers no rows), so the occupancy reads are skipped
        // and the rest is reached: the property ids, the revenue and the cash of each period, the arrivals, the next arrivals.
        ["overview-month"] = (db, scope) => Dashboard(db).GetOverviewAsync(
            scope, new HostOverviewQuery(HostDashboardPeriodKind.Month, new DateOnly(2026, 10, 1), IncludeMoney: true)),
        ["overview-without-money"] = (db, scope) => Dashboard(db).GetOverviewAsync(
            scope, new HostOverviewQuery(HostDashboardPeriodKind.Next30Days)),
        ["overview-custom-year"] = (db, scope) => Dashboard(db).GetOverviewAsync(
            scope,
            new HostOverviewQuery(HostDashboardPeriodKind.Custom, From: new DateOnly(2026, 1, 1), To: new DateOnly(2026, 12, 31), IncludeMoney: true)),
        ["overview-one-property"] = (db, scope) => Dashboard(db).GetOverviewAsync(
            scope, new HostOverviewQuery(HostDashboardPeriodKind.Last30Days, PropertyId: Guid.NewGuid(), IncludeMoney: true)),
        // The reads of the day that need a stay to run: the check-in link and the Alloggiati report of the arrivals, the cleaning of the
        // departures (the empty answer of the probe skips them in "today").
        ["today-arriving-and-leaving"] = async (db, scope) =>
        {
            var service = HostTodayServiceTests.NewService(db);
            var stay = new HostDashboardStay(
                Guid.NewGuid(), Guid.NewGuid(), "Trullo", "Anna Verdi", new DateTime(2026, 10, 9, 0, 0, 0, DateTimeKind.Utc),
                new DateTime(2026, 10, 12, 0, 0, 0, DateTimeKind.Utc), BookingStatus.Confirmed, 300m, new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc));
            await service.GetArrivingAsync([stay], new DateTime(2026, 10, 9, 0, 0, 0, DateTimeKind.Utc), HostScopeScenario.Now.UtcDateTime, default);
            await service.GetCleaningAsync(scope, [stay], default);
        },
        ["bookings-search-view-to-confirm"] = (db, scope) => Search(db).SearchAsync(scope, new BookingSearchCriteria(View: BookingListView.ToConfirm)),
        ["bookings-search-view-upcoming"] = (db, scope) => Search(db).SearchAsync(scope, new BookingSearchCriteria(View: BookingListView.Upcoming)),
        ["bookings-search-view-in-progress"] = (db, scope) => Search(db).SearchAsync(scope, new BookingSearchCriteria(View: BookingListView.InProgress)),
        ["bookings-search-view-completed"] = (db, scope) => Search(db).SearchAsync(scope, new BookingSearchCriteria(View: BookingListView.Completed)),
        ["bookings-search-view-cancelled"] = (db, scope) => Search(db).SearchAsync(scope, new BookingSearchCriteria(View: BookingListView.Cancelled)),
        ["bookings-search-channels-arrivals-and-links"] = (db, scope) => Search(db).SearchAsync(scope, EveryNewCriterion),
        ["bookings-search-link-sent"] = (db, scope) => Search(db).SearchAsync(
            scope, new BookingSearchCriteria(CheckInStates: [HostTodayCheckInState.Sent])),
        ["bookings-search-link-in-progress-or-completed"] = (db, scope) => Search(db).SearchAsync(
            scope, new BookingSearchCriteria(CheckInStates: [HostTodayCheckInState.InProgress, HostTodayCheckInState.Completed])),
        ["bookings-search-link-not-sent-or-expired"] = (db, scope) => Search(db).SearchAsync(
            scope, new BookingSearchCriteria(CheckInStates: [HostTodayCheckInState.NotSent, HostTodayCheckInState.Expired])),
        ["bookings-search-sorted-by-check-in"] = (db, scope) => Search(db).SearchAsync(scope, new BookingSearchCriteria(Sort: new BookingSort(BookingSortKey.CheckIn))),
        ["bookings-search-sorted-by-created-at"] = (db, scope) => Search(db).SearchAsync(scope, new BookingSearchCriteria(Sort: new BookingSort(BookingSortKey.CreatedAt, true))),
        ["bookings-search-sorted-by-total"] = (db, scope) => Search(db).SearchAsync(scope, new BookingSearchCriteria(Sort: new BookingSort(BookingSortKey.Total))),
        ["bookings-search-sorted-by-status"] = (db, scope) => Search(db).SearchAsync(scope, new BookingSearchCriteria(Sort: new BookingSort(BookingSortKey.Status, true))),
        ["bookings-search-sorted-by-guest"] = (db, scope) => Search(db).SearchAsync(scope, new BookingSearchCriteria(Sort: new BookingSort(BookingSortKey.Guest))),
        ["bookings-search-sorted-by-guest-descending"] = (db, scope) => Search(db).SearchAsync(scope, new BookingSearchCriteria(Sort: new BookingSort(BookingSortKey.Guest, true))),
        ["bookings-search-row-details"] = (db, scope) => Search(db).DetailsOfAsync(
            scope,
            [new Booking { Id = Guid.NewGuid(), Status = BookingStatus.Confirmed, CheckInDate = new DateTime(2026, 10, 9, 0, 0, 0, DateTimeKind.Utc) }],
            true,
            new DateTime(2026, 10, 9, 0, 0, 0, DateTimeKind.Utc),
            default),
        ["bookings-counts-everything"] = (db, scope) => Search(db).CountAsync(scope, EveryNewCriterion),
        ["bookings-counts-plain"] = (db, scope) => Search(db).CountAsync(scope, new BookingSearchCriteria()),
        ["payments-list-page-3"] = (db, scope) => new PaymentListService(db).ListPageAsync(
            scope,
            new PaymentListCriteria(Guid.NewGuid(), Guid.NewGuid(), new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 31)),
            3,
            10),
    };

    /// <summary>How many statements each call sends at least (all of them are reached when nothing was swallowed early).</summary>
    private static readonly Dictionary<string, int> MinimumStatements = new()
    {
        // The property ids, then per period the revenue and the cash, the three lists of the day and the latest bookings.
        ["kpis-month"] = 8,
        ["kpis-last-30-days"] = 8,
        ["kpis-next-30-days"] = 8,
        ["kpis-without-money"] = 6,
        // The property is not the caller's here (the probe answers "no" to the existence check): the call stops with a 404.
        ["kpis-one-property"] = 1,
        ["today-stays"] = 3,
        ["today"] = 10,
        ["today-without-payments"] = 9,
        ["cockpit-missing"] = 3,
        ["bookings-search-everything"] = 1,
        ["bookings-search-plain-text"] = 1,
        // A page past the end of nothing: the page is empty and the count is asked.
        ["bookings-search-page-3"] = 2,
        ["payments-list-everything"] = 1,
        ["payments-list-plain"] = 1,
        // SR-03b: the property ids, the revenue and the cash of each period, the arrivals and the next arrivals; without money, no cash;
        // one property that is not the caller's stops at the check.
        ["overview-month"] = 7,
        ["overview-without-money"] = 5,
        ["overview-custom-year"] = 7,
        ["overview-one-property"] = 1,
        // The booking, the links, the reports; the cleaning.
        ["today-arriving-and-leaving"] = 4,
        ["bookings-search-view-to-confirm"] = 1,
        ["bookings-search-view-upcoming"] = 1,
        ["bookings-search-view-in-progress"] = 1,
        ["bookings-search-view-completed"] = 1,
        ["bookings-search-view-cancelled"] = 1,
        ["bookings-search-channels-arrivals-and-links"] = 1,
        ["bookings-search-link-sent"] = 1,
        ["bookings-search-link-in-progress-or-completed"] = 1,
        ["bookings-search-link-not-sent-or-expired"] = 1,
        ["bookings-search-sorted-by-check-in"] = 1,
        ["bookings-search-sorted-by-created-at"] = 1,
        ["bookings-search-sorted-by-total"] = 1,
        ["bookings-search-sorted-by-status"] = 1,
        ["bookings-search-sorted-by-guest"] = 1,
        ["bookings-search-sorted-by-guest-descending"] = 1,
        // The payments and the Alloggiati reports of the page.
        ["bookings-search-row-details"] = 2,
        // One aggregate statement.
        ["bookings-counts-everything"] = 1,
        ["bookings-counts-plain"] = 1,
        // A page past the end of nothing: the page is empty and the count is asked.
        ["payments-list-page-3"] = 2,
    };

    public static TheoryData<string, string> EveryCallWithEveryScope()
    {
        var data = new TheoryData<string, string>();
        foreach (var call in Calls.Keys)
        {
            foreach (var scope in new[] { "collaborator", "account-in-no-team", "whole-org" })
                data.Add(call, scope);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(EveryCallWithEveryScope))]
    public async Task Call_IsTranslatedToSqlByNpgsql(string call, string scope)
    {
        var statements = new List<string>();
        await using var db = NpgsqlTranslationProbe.NewContext(statements);

        await NpgsqlTranslationProbe.AssertTranslatesAsync(() => Calls[call](db, ScopeOf(scope)));

        // The probe must have got as far as the last query of the call, or it proved nothing about the ones after the first.
        Assert.True(
            statements.Count >= MinimumStatements[call],
            $"{call}: only {statements.Count} of the expected {MinimumStatements[call]} statements were reached.");
    }

    [Fact]
    public void EveryCall_HasItsMinimum()
    {
        Assert.Equal(Calls.Keys.Order(), MinimumStatements.Keys.Order());
    }

    [Fact]
    public async Task TheScopeIsPartOfTheSql_OneExistsOnTheGrants_NeverAListOfIds()
    {
        var bookingStatements = new List<string>();
        await using (var db = NpgsqlTranslationProbe.NewContext(bookingStatements))
        {
            await NpgsqlTranslationProbe.AssertTranslatesAsync(
                () => new BookingSearchService(db).SearchAsync(ScopeOf("collaborator"), EveryCriterion));
        }

        var paymentStatements = new List<string>();
        await using (var db = NpgsqlTranslationProbe.NewContext(paymentStatements))
        {
            await NpgsqlTranslationProbe.AssertTranslatesAsync(
                () => new PaymentListService(db).ListAsync(ScopeOf("collaborator"), new PaymentListCriteria()));
        }

        // The page of an empty answer is the whole answer: one statement each.
        var bookings = Assert.Single(bookingStatements);
        var payments = Assert.Single(paymentStatements);
        foreach (var sql in new[] { bookings, payments })
        {
            Assert.Contains("PropertyMemberAccesses", sql, StringComparison.Ordinal);
            Assert.Contains("EXISTS", sql, StringComparison.Ordinal);
        }

        // The page is cut in SQL, in a total order.
        Assert.Contains("LIMIT", bookings, StringComparison.Ordinal);
        Assert.Contains("OFFSET", bookings, StringComparison.Ordinal);
        Assert.Contains("ORDER BY", bookings, StringComparison.Ordinal);
    }
}
