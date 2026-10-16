using Casazen.Core.Entities.Enums;
using Casazen.Core.Suppliers;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Casazen.Tests.Unit.Infrastructure;

/// <summary>
/// SP-04e: the LINQ of the new queries (the overview of the home, the counters of the tabs, the payments summed or counted, the filters
/// <c>source</c>, <c>q</c> and the days of <c>today?days=</c>) becomes SQL on the PostgreSQL provider without a server:
/// <c>ToQueryString</c> compiles the query, so a construct that only works in memory fails here instead of on the first request in
/// production. The results on real rows are checked by the in-memory tests and, on PostgreSQL, by <c>SupplierReadModelsPostgresTests</c> (CI).
/// </summary>
public class SupplierReadModelsSqlTests
{
    private static readonly Guid SupplierOrg = Guid.Parse("00000000-0000-0000-0000-0000000000a1");

    private static readonly DateTimeOffset Instant = new(2026, 10, 8, 10, 0, 0, TimeSpan.Zero);

    private static readonly DateTime PreviousStart = new(2026, 9, 22, 22, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime CurrentStart = new(2026, 9, 30, 22, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime CurrentEnd = new(2026, 10, 8, 22, 0, 0, DateTimeKind.Utc);

    // ─── The overview ───

    [Fact]
    public void OverviewCounts_AreCountedByTheDatabaseInOnePass_ForTheSupplierOnly()
    {
        using var db = NewNpgsqlContext();

        var sql = SupplierKpiService.OverviewCountsOf(db, SupplierOrg, PreviousStart, CurrentStart, CurrentEnd).ToQueryString();

        Assert.Matches("\"SupplierOrgId\" = @", sql);
        Assert.Matches("(?i)count\\(\\*\\)", sql);
        // Arrived (CreatedAt), taken (TakenAt), refused (a final status and its last update), completed (CompletedAt).
        Assert.Contains("\"CreatedAt\" >= @", sql);
        Assert.Contains("\"TakenAt\" >= @", sql);
        Assert.Contains("\"UpdatedAt\" >= @", sql);
        Assert.Contains("\"CompletedAt\" >= @", sql);
        Assert.Matches("\"Status\" = 5", sql); // Rifiutato
        Assert.Matches("\"Status\" = 3|\"Status\" IN \\(3, 4\\)|\"Status\" = 4", sql); // Completato, Pagato
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(sql, "FROM \"ServiceRequests\""));
    }

    [Fact]
    public void OverviewAnswers_AreThePairsOfTheRequestsTakenInTheWindow_ForTheSupplierOnly()
    {
        using var db = NewNpgsqlContext();

        var sql = SupplierKpiService.AnswersBetween(db, SupplierOrg, PreviousStart, CurrentEnd).ToQueryString();

        Assert.Matches("\"SupplierOrgId\" = @", sql);
        Assert.Contains("\"TakenAt\" IS NOT NULL", sql);
        Assert.Contains("\"TakenAt\" >= @", sql);
        Assert.Contains("\"TakenAt\" < @", sql);
    }

    [Fact]
    public void OverviewOpenJobs_AreTheCompletedNotPaidOnesOfTheSupplier()
    {
        using var db = NewNpgsqlContext();

        var sql = SupplierKpiService.OpenJobsOf(db, SupplierOrg).ToQueryString();

        Assert.Matches("\"SupplierOrgId\" = @", sql);
        Assert.Matches("\"Status\" = 3", sql);
        Assert.Contains("\"FinalAmountCents\"", sql);
    }

    [Fact]
    public void PaidPayments_AreSummedByTheDatabase_ForTheSupplierOnly_AndOnlyThePaidOnesInTheWindow()
    {
        using var db = NewNpgsqlContext();

        var sql = SupplierPaymentReadModels.PaidBetween(db, SupplierOrg, CurrentStart, CurrentEnd).ToQueryString();

        Assert.Matches("\"SupplierOrgId\" = @", sql);
        Assert.Matches($"\"Status\" = {(int)ServicePaymentStatus.Paid}", sql);
        Assert.Contains("\"PaidAt\" >= @", sql);
        Assert.Contains("\"PaidAt\" < @", sql);
        Assert.Matches("(?i)sum\\(", sql);
        Assert.Contains("\"AmountCents\"", sql);
        Assert.Contains("\"ApplicationFeeCents\"", sql);
        Assert.Contains("\"NetCents\"", sql);
        Assert.Contains("FROM \"ServiceRequestPayments\"", sql);
    }

    // ─── The counters ───

    [Fact]
    public void TabCounts_AreCountedByTheDatabaseInOnePass_ForTheSupplierOnly()
    {
        using var db = NewNpgsqlContext();

        var sql = SupplierServiceRequestReader.TabCountsOf(db, SupplierOrg).ToQueryString();

        Assert.Matches("\"SupplierOrgId\" = @", sql);
        Assert.Matches("(?i)count\\(\\*\\)", sql);
        Assert.Contains("\"ProposedStartUtc\" IS NULL", sql);
        Assert.Contains("\"ProposedStartUtc\" IS NOT NULL", sql);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(sql, "FROM \"ServiceRequests\""));
        // The counters read the requests only: no join with the property, the org, the booking or the customer.
        Assert.DoesNotContain("JOIN", sql, StringComparison.OrdinalIgnoreCase);
    }

    // ─── The filters ───

    [Fact]
    public void InboxFilter_TheTabsOfTheProposal_UseTheProposedTimeOfTheRequest()
    {
        using var db = NewNpgsqlContext();
        var reader = Reader(db);

        var toAnswer = reader.Filter(reader.Rows(SupplierOrg), Query(awaitingCustomer: false)).ToQueryString();
        var awaiting = reader.Filter(reader.Rows(SupplierOrg), Query(awaitingCustomer: true)).ToQueryString();

        Assert.Contains("\"ProposedStartUtc\" IS NULL", toAnswer);
        Assert.Contains("\"ProposedStartUtc\" IS NOT NULL", awaiting);
        Assert.Matches("\"SupplierOrgId\" = @", toAnswer);
    }

    [Fact]
    public void InboxFilter_Source_IsTheOriginOfTheRequest()
    {
        using var db = NewNpgsqlContext();
        var reader = Reader(db);

        var sql = reader.Filter(reader.Rows(SupplierOrg), Query(source: ServiceRequestSource.Showcase)).ToQueryString();

        Assert.Matches("\"Source\" = @", sql);
        Assert.Contains($"@source='{(int)ServiceRequestSource.Showcase}'", sql);
    }

    [Fact]
    public void InboxFilter_Text_SearchesTheServiceNameTheCategoryAndTheComuneAndNothingElse()
    {
        using var db = NewNpgsqlContext();
        var reader = Reader(db);

        var sql = reader.Filter(reader.Rows(SupplierOrg), Query(text: "a%b_c")).ToQueryString();
        var where = sql[sql.IndexOf("WHERE", StringComparison.Ordinal)..];

        Assert.Matches("(?i)lower\\(", where);
        Assert.Contains("\"ServiceNameSnapshot\"", where);
        Assert.Contains("\"Category\"", where);
        // The text is a parameter of a LIKE, and the provider escapes what it holds: a percent or an underscore typed by the supplier is
        // the character it is, not a wildcard (PostgreSQL's escape character is the backslash).
        Assert.Contains("LIKE @term_contains", where);
        Assert.Contains("@term_contains='%a\\%b\\_c%'", sql);
        Assert.DoesNotContain("'%a%b_c%'", sql, StringComparison.Ordinal);
        // What the take discloses is never searched.
        Assert.DoesNotContain("\"Notes\"", where, StringComparison.Ordinal);
        Assert.DoesNotContain("\"Address\"", where, StringComparison.Ordinal);
        Assert.DoesNotContain("\"LocationAddress\"", where, StringComparison.Ordinal);
        Assert.DoesNotContain("\"LocationFloor\"", where, StringComparison.Ordinal);
        Assert.DoesNotContain("\"LocationAccessNotes\"", where, StringComparison.Ordinal);
        Assert.DoesNotContain("\"FullName\"", where, StringComparison.Ordinal);
        Assert.DoesNotContain("\"DisplayName\"", where, StringComparison.Ordinal);
        Assert.DoesNotContain("\"Email\"", where, StringComparison.Ordinal);
        Assert.DoesNotContain("\"PublicCode\"", where, StringComparison.Ordinal); // a text is not a whole code
    }

    [Fact]
    public void InboxFilter_Text_ThatIsAWholeBookingCode_AlsoComparesTheCodeForEquality()
    {
        using var db = NewNpgsqlContext();
        var reader = Reader(db);

        var sql = reader.Filter(reader.Rows(SupplierOrg), Query(text: "7Z9Q2-X4W8V")).ToQueryString();
        var where = sql[sql.IndexOf("WHERE", StringComparison.Ordinal)..];

        Assert.Matches("\"PublicCode\" = @", where);
        Assert.DoesNotContain("\"PublicCode\" LIKE", where, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("lower(s.\"PublicCode\")", where, StringComparison.Ordinal);
        // The code that is compared is the stored form: no dash, upper case.
        Assert.Contains("@bookingCode='7Z9Q2X4W8V'", sql);
    }

    [Fact]
    public void InboxFilter_TheDaysOfTheJob_AreTheScheduledTimeElseTheCheckOutDay()
    {
        using var db = NewNpgsqlContext();
        var reader = Reader(db);
        var query = new SupplierInboxQuery(
            [ServiceRequestStatus.PresoInCarico],
            null,
            null,
            1,
            50,
            SupplierInboxSort.WorkTime,
            WorkDays: new SupplierInboxDayRange(new DateOnly(2026, 10, 8), new DateOnly(2026, 10, 14)));

        var sql = reader.Filter(reader.Rows(SupplierOrg), query).ToQueryString();

        Assert.Contains("\"ScheduledStartUtc\"", sql);
        Assert.Contains("\"CheckOutDate\"", sql);
        Assert.Matches("(?i)coalesce\\(", sql);
    }

    [Fact]
    public void InboxFilter_NoNewFilter_AddsNothingToTheStatementOfBefore()
    {
        // nuove, programmate, ... are the statements they were: the new parameters of the query add no predicate unless they are given.
        using var db = NewNpgsqlContext();
        var reader = Reader(db);
        var plain = new SupplierInboxQuery([ServiceRequestStatus.Richiesto], null, null, 1, 20, SupplierInboxSort.Urgency);
        var explicitNothing = plain with { Source = null, Text = null, AwaitingCustomer = null, WorkDays = null };

        Assert.Equal(
            reader.Filter(reader.Rows(SupplierOrg), plain).ToQueryString(),
            reader.Filter(reader.Rows(SupplierOrg), explicitNothing).ToQueryString());
        Assert.DoesNotContain("\"Source\" =", reader.Filter(reader.Rows(SupplierOrg), plain).ToQueryString(), StringComparison.Ordinal);
    }

    [Fact]
    public void InboxRows_TheBookingCodeIsRead_OnlyToBeSearched_NeverNamedByTheViews()
    {
        // PublicCode is a column of the row so the filter can compare it; the view of the console has no member for it.
        Assert.DoesNotContain(
            typeof(SupplierServiceRequestView).GetProperties(),
            property => property.Name.Contains("Code", StringComparison.OrdinalIgnoreCase));
    }

    // ─── helpers ───

    private static SupplierInboxQuery Query(
        bool? awaitingCustomer = null,
        ServiceRequestSource? source = null,
        string? text = null) =>
        new([ServiceRequestStatus.Richiesto], null, null, 1, 20, SupplierInboxSort.Urgency, Source: source, Text: text, AwaitingCustomer: awaitingCustomer);

    private static SupplierServiceRequestReader Reader(AppDbContext db) => new(db, new FixedTimeProvider(Instant));

    private static AppDbContext NewNpgsqlContext() =>
        new(
            new DbContextOptionsBuilder<AppDbContext>()
                .UseNpgsql(
                    "Host=localhost;Database=casazen_design;Username=postgres;Password=postgres",
                    npgsql => npgsql.MigrationsAssembly("Casazen.Infrastructure"))
                .Options);
}
