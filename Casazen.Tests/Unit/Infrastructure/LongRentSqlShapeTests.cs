using Casazen.Core.Authorization;
using Casazen.Core.Leases;
using Casazen.Core.Services;
using Casazen.Infrastructure.Repositories;
using Casazen.Infrastructure.Services;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Casazen.Tests.Unit.Infrastructure;

/// <summary>
/// LR-01: the SQL that the lease list, the rent register, the agenda and the overview become on the real provider (Npgsql; nothing is
/// executed, see <see cref="LongRentSqlProbe"/>). What EF InMemory cannot prove: every query is translated (none is evaluated on the
/// client), the number of statements does not depend on the number of rows (no query per lease: the first tenant and the rent ledger
/// are part of the one statement of the list), the reach of the caller and the text looked for are parameters of the statement, never
/// text inside it, and the page is ordered by a total order. The data-level proof is in the integration tests (InMemory, and
/// PostgreSQL in CI, which also counts the commands of a real database).
/// </summary>
public class LongRentSqlShapeTests
{
    private const string Owner = "auth0|proprietario-segreto";
    private const string SearchText = "cognomesegreto";

    private static readonly Guid OrgId = Guid.Parse("7a3c1d52-9f0e-4b8a-8d65-2c4e1f0b9a11");
    private static readonly HostScope Restricted = new(OrgId, Owner);
    private static readonly HostScope OrgWide = new(OrgId, null);
    private static readonly DateTime Today = new(2026, 10, 9, 0, 0, 0, DateTimeKind.Utc);

    private sealed class Clock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(Today.AddHours(8), TimeSpan.Zero);
    }

    private static int Count(string text, string part) => LongRentSqlProbe.Count(text, part);

    // --- The lease list ---------------------------------------------------------------------------------

    [Theory]
    [InlineData(LeaseListView.All)]
    [InlineData(LeaseListView.Active)]
    [InlineData(LeaseListView.InPreparation)]
    [InlineData(LeaseListView.Expiring)]
    [InlineData(LeaseListView.Ended)]
    public async Task LeaseList_EveryView_IsOneStatement(LeaseListView view)
    {
        var statements = new List<string>();
        await using var db = LongRentSqlProbe.NewContext(statements);

        await new LeaseContractRepository(db).GetSummariesAsync(Restricted, new LeaseListQuery(Guid.NewGuid(), view, SearchText), Today);

        var sql = Assert.Single(statements);
        // The rent ledger is joined once, grouped by lease; the first tenant is read by scalar subqueries on the unique index of the parties.
        Assert.Equal(1, Count(sql, "LEFT JOIN"));
        Assert.Equal(1, Count(sql, "GROUP BY"));
        Assert.Equal(1, Count(sql, "FROM \"RentLedgerEntries\""));
        Assert.Equal(3, Count(sql, "LIMIT 1"));
        Assert.DoesNotContain("ROW_NUMBER", sql, StringComparison.Ordinal);
        Assert.Contains("ORDER BY l.\"CreatedAt\" DESC, l.\"Id\"", sql, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LeaseList_TheReachOfTheCallerAndTheSearchText_AreParametersNeverTextInTheStatement()
    {
        var statements = new List<string>();
        await using var db = LongRentSqlProbe.NewContext(statements);

        await new LeaseContractRepository(db).GetSummariesAsync(Restricted, new LeaseListQuery(null, LeaseListView.All, SearchText), Today);

        var sql = Assert.Single(statements);
        Assert.Contains("\"OwnerId\" = @", sql, StringComparison.Ordinal);
        Assert.DoesNotContain(Owner, sql, StringComparison.Ordinal);
        Assert.DoesNotContain(SearchText, sql, StringComparison.Ordinal);
        Assert.DoesNotContain(" IN (SELECT", sql, StringComparison.Ordinal);
        Assert.Matches(@"lower\(p0\.""Name""\) LIKE @", sql);
        // Only the tenants who were not anonymized can be found by name.
        Assert.Contains("\"AnonymizedAt\" IS NULL", sql, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LeaseList_AnOrgWideCaller_HasNoOwnerPredicate_OnlyTheOrg()
    {
        var statements = new List<string>();
        await using var db = LongRentSqlProbe.NewContext(statements);

        await new LeaseContractRepository(db).GetSummariesAsync(OrgWide, new LeaseListQuery(), Today);

        var sql = Assert.Single(statements);
        Assert.DoesNotContain("\"OwnerId\" = @", sql, StringComparison.Ordinal);
        Assert.Contains("l.\"OrgId\" = @", sql, StringComparison.Ordinal);
        // The ledger is restricted to the org too, inside its subquery.
        Assert.Equal(2, Count(sql, "\"OrgId\" = @"));
    }

    [Fact]
    public async Task LeaseList_TheRentColumns_AreTheRulesOfTheLedger()
    {
        var statements = new List<string>();
        await using var db = LongRentSqlProbe.NewContext(statements);

        await new LeaseContractRepository(db).GetSummariesAsync(OrgWide, new LeaseListQuery(), Today);

        var sql = Assert.Single(statements);
        // Still to be collected: scheduled (0), processing (1) and failed (3). Overdue: scheduled or failed (0, 3) and past due.
        Assert.Contains("\"Status\" IN (0, 1, 3)", sql, StringComparison.Ordinal);
        Assert.Contains("\"Status\" IN (0, 3) AND r.\"DueDate\" < @", sql, StringComparison.Ordinal);
    }

    // --- The rent register ------------------------------------------------------------------------------

    /// <summary>The numbers of the month: one bucket with 40 installments, so that there is a page to read.</summary>
    private static Func<string, System.Data.DataTable?> CountersAnswer(int bucket = 2) => sql => sql.Contains("GROUP BY", StringComparison.Ordinal)
        ? LongRentSqlProbe.Row(("Bucket", typeof(int), bucket), ("Count", typeof(int), 40), ("Amount", typeof(decimal), 36_000m))
        : null;

    [Theory]
    [InlineData(RentRegisterStatus.All, 2)]
    [InlineData(RentRegisterStatus.Paid, 0)]
    [InlineData(RentRegisterStatus.Pending, 1)]
    [InlineData(RentRegisterStatus.Overdue, 2)]
    public async Task Register_IsThreeStatementsWhateverTheStatusAndThePage(RentRegisterStatus status, int bucketWithRows)
    {
        var statements = new List<string>();
        await using var db = LongRentSqlProbe.NewContext(statements, CountersAnswer(bucketWithRows));

        await new RentRegisterService(db, new ConfigurationBuilder().Build(), new Clock())
            .GetRegisterAsync(Restricted, new RentRegisterQuery(new DateOnly(2026, 10, 1), status, 2, 10));

        // The numbers of the month, the page, the Stripe state of the org: always these three.
        Assert.Equal(3, statements.Count);
        Assert.Contains("GROUP BY", statements[0], StringComparison.Ordinal);
        Assert.Contains("LIMIT", statements[1], StringComparison.Ordinal);
        Assert.Contains("FROM \"Orgs\"", statements[2], StringComparison.Ordinal);
    }

    [Fact]
    public async Task Register_ThePageIsOrderedByDueDateThenId_AndReadsOnePageOfRows()
    {
        var statements = new List<string>();
        await using var db = LongRentSqlProbe.NewContext(statements, CountersAnswer());

        await new RentRegisterService(db, new ConfigurationBuilder().Build(), new Clock())
            .GetRegisterAsync(Restricted, new RentRegisterQuery(new DateOnly(2026, 10, 1), RentRegisterStatus.Overdue, 2, 25));

        var page = statements[1];
        // A total order (the id is unique), so a page never repeats or skips a row.
        Assert.Contains("ORDER BY r.\"DueDate\", r.\"Id\"", page, StringComparison.Ordinal);
        Assert.Matches(@"LIMIT @\w+ OFFSET @\w+", page);
        // The month by due date, the reach of the caller, the overdue rule and the exclusion of the cancelled installments (4).
        Assert.Contains("r.\"DueDate\" >= @", page, StringComparison.Ordinal);
        Assert.Contains("r.\"DueDate\" <= @", page, StringComparison.Ordinal);
        Assert.Contains("\"OwnerId\" = @", page, StringComparison.Ordinal);
        Assert.Contains("r.\"Status\" <> 4", page, StringComparison.Ordinal);
        Assert.Contains("r.\"Status\" IN (0, 3) AND r.\"DueDate\" < @", page, StringComparison.Ordinal);
        // The first tenant of the page's leases: scalar subqueries, no join of the parties of the whole table.
        Assert.DoesNotContain("ROW_NUMBER", page, StringComparison.Ordinal);
        Assert.Equal(3, Count(page, "LIMIT 1"));
        Assert.DoesNotContain(Owner, page, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Register_AnEmptyMonth_ReadsNoPage()
    {
        var statements = new List<string>();
        await using var db = LongRentSqlProbe.NewContext(statements);

        var page = await new RentRegisterService(db, new ConfigurationBuilder().Build(), new Clock())
            .GetRegisterAsync(OrgWide, new RentRegisterQuery(new DateOnly(2031, 1, 1)));

        Assert.Equal(0, page.Total);
        Assert.Empty(page.Items);
        // The numbers (0) and the org: no page to read when the numbers say there is none.
        Assert.Equal(2, statements.Count);
    }

    [Fact]
    public async Task Register_TheCountersAreOneGroupedStatement_WithTheSameRulesAsTheLedger()
    {
        var statements = new List<string>();
        await using var db = LongRentSqlProbe.NewContext(statements);

        await new RentRegisterService(db, new ConfigurationBuilder().Build(), new Clock())
            .GetRegisterAsync(OrgWide, new RentRegisterQuery(new DateOnly(2026, 10, 1)));

        var counters = statements[0];
        Assert.Equal(1, Count(counters, "GROUP BY"));
        Assert.Contains("WHEN r.\"Status\" = 2 THEN 0", counters, StringComparison.Ordinal); // paid: collected
        Assert.Contains("WHEN r.\"Status\" IN (0, 3) AND r.\"DueDate\" < @", counters, StringComparison.Ordinal); // overdue
        Assert.Contains("r.\"Status\" <> 4", counters, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FindInstallments_IsOneStatement_TheIdsAreAParameter()
    {
        var statements = new List<string>();
        await using var db = LongRentSqlProbe.NewContext(statements);
        var ids = Enumerable.Range(0, 50).Select(_ => Guid.NewGuid()).ToList();

        await new RentRegisterService(db, new ConfigurationBuilder().Build(), new Clock()).FindInstallmentsAsync(ids);

        var sql = Assert.Single(statements);
        Assert.Equal(0, Count(sql, ids[0].ToString()));
        Assert.Matches(@"= ANY \(@", sql);
    }

    // --- The agenda and the overview --------------------------------------------------------------------

    [Fact]
    public async Task Deadlines_AreTwoStatements_TheLeasesAndTheInstallments_NotOnePerLease()
    {
        var statements = new List<string>();
        await using var db = LongRentSqlProbe.NewContext(statements);

        await new LongRentAgendaService(db, new Clock()).GetDeadlinesAsync(Restricted, new LongRentDeadlinesQuery(new DateOnly(2026, 10, 9), new DateOnly(2027, 1, 7)));

        Assert.Equal(2, statements.Count);
        Assert.Contains("FROM \"LeaseContracts\"", statements[0], StringComparison.Ordinal);
        Assert.Contains("FROM \"RentLedgerEntries\"", statements[1], StringComparison.Ordinal);
        // Only the leases that can still carry a deadline: not rejected (7), not ended.
        Assert.Contains("l.\"Status\" <> 7 AND l.\"EndDate\" >= @", statements[0], StringComparison.Ordinal);
        // The installments: the earliest 501 (the limit and one more, to know it was cut), by due date then id, unpaid (0, 3).
        Assert.Contains("r.\"Status\" IN (0, 3)", statements[1], StringComparison.Ordinal);
        Assert.Contains("ORDER BY r.\"DueDate\", r.\"Id\"", statements[1], StringComparison.Ordinal);
        Assert.Contains("LIMIT @", statements[1], StringComparison.Ordinal);
        Assert.DoesNotContain(Owner, string.Concat(statements), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(LongRentDeadlineType.Rent, 1, "RentLedgerEntries")]
    [InlineData(LongRentDeadlineType.Questura, 1, "LeaseContracts")]
    [InlineData(LongRentDeadlineType.LeaseEnd, 1, "LeaseContracts")]
    [InlineData(LongRentDeadlineType.Notice, 1, "LeaseContracts")]
    [InlineData(LongRentDeadlineType.RliRegistration, 1, "LeaseContracts")]
    public async Task Deadlines_OfOneType_ReadOnlyWhatThatTypeNeeds(LongRentDeadlineType type, int expected, string table)
    {
        var statements = new List<string>();
        await using var db = LongRentSqlProbe.NewContext(statements);

        await new LongRentAgendaService(db, new Clock()).GetDeadlinesAsync(OrgWide, new LongRentDeadlinesQuery(new DateOnly(2026, 10, 9), new DateOnly(2027, 1, 7), type));

        Assert.Equal(expected, statements.Count);
        Assert.Contains($"FROM \"{table}\"", statements[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task Overview_IsAFixedNumberOfStatements_WhateverTheNumberOfLeases()
    {
        var statements = new List<string>();
        await using var db = LongRentSqlProbe.NewContext(statements);

        await new LongRentAgendaService(db, new Clock()).GetOverviewAsync(Restricted);

        // The leases counted by status, the numbers of the month, the installments behind, the leases that can carry a deadline, and
        // the installments of the next days.
        Assert.Equal(5, statements.Count);
        Assert.Contains("GROUP BY s.\"Status\", s.\"Ended\", s.\"Soon\"", statements[0], StringComparison.Ordinal);
        Assert.Contains("GROUP BY s.\"Key\"", statements[1], StringComparison.Ordinal);
        Assert.Contains("GROUP BY r.\"LeaseContractId\"", statements[2], StringComparison.Ordinal);
        Assert.DoesNotContain(Owner, string.Concat(statements), StringComparison.Ordinal);
    }
}
