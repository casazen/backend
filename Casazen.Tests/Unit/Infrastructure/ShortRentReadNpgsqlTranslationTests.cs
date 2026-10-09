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
