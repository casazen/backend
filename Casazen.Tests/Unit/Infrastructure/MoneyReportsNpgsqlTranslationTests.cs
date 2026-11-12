using Casazen.Core.Authorization;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Services;
using Casazen.Tests.Unit.Services;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Casazen.Tests.Unit.Infrastructure;

/// <summary>
/// NF-97, NF-98: every read of the money reports, run on the PostgreSQL provider with the three kinds of scope, without a server (see
/// <see cref="NpgsqlTranslationProbe"/>). InMemory evaluates a query the way .NET does and accepts what Npgsql cannot turn into SQL (a subquery over the payments of
/// a booking inside a comparison, an expression shared with another query); this fails here, before CI, on a query that has no translation, and shows what the SQL
/// does and does not touch.
/// </summary>
public class MoneyReportsNpgsqlTranslationTests
{
    private static readonly Guid OrgId = Guid.Parse("7a3c1d52-9f0e-4b8a-8d65-2c4e1f0b9a11");

    private static HostScope ScopeOf(string kind) => kind switch
    {
        "collaborator" => new HostScope(OrgId, GrantedToUserId: "auth0|collaboratore"),
        "account-in-no-team" => new HostScope(OrgId, OwnerId: "auth0|titolare"),
        _ => new HostScope(OrgId),
    };

    private static HostCollectionsReportService Collections(AppDbContext db) => new(db, new ConfigurationBuilder().Build(), MoneyReportsKit.Clock);

    private static readonly HostReportQuery October = new(new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 31));

    /// <summary>Every call, by the name the failure reports.</summary>
    private static readonly Dictionary<string, Func<AppDbContext, HostScope, Task>> Calls = new()
    {
        ["collections"] = (db, scope) => Collections(db).GetCollectionsAsync(scope, October),
        ["collections-long-period"] = (db, scope) =>
            Collections(db).GetCollectionsAsync(scope, new HostReportQuery(new DateOnly(2025, 1, 1), new DateOnly(2026, 12, 31))),
        ["balances-to-collect"] = (db, scope) => Collections(db).GetBalancesToCollectAsync(scope, new HostBalancesQuery()),
    };

    /// <summary>How many statements each call sends at least (all of them are reached when nothing was swallowed early).</summary>
    private static readonly Dictionary<string, int> MinimumStatements = new()
    {
        // The collected payments, the credits of the card provider, the credits of the channels: three whatever the number of payments.
        ["collections"] = 3,
        ["collections-long-period"] = 3,
        // The stays of CasaZen's money, the stays of the channel manager's: two whatever the number of stays.
        ["balances-to-collect"] = 2,
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
    public async Task TheScopeIsPartOfEveryStatement_OneExistsOnTheGrants_NeverAListOfIds()
    {
        foreach (var call in Calls.Keys)
        {
            var statements = new List<string>();
            await using var db = NpgsqlTranslationProbe.NewContext(statements);

            await NpgsqlTranslationProbe.AssertTranslatesAsync(() => Calls[call](db, ScopeOf("collaborator")));

            Assert.True(statements.Count >= MinimumStatements[call], $"{call}: the statements were not all reached.");
            foreach (var sql in statements)
            {
                Assert.Contains("PropertyMemberAccesses", sql, StringComparison.Ordinal);
                Assert.Contains("EXISTS", sql, StringComparison.Ordinal);
            }
        }
    }

    [Fact]
    public async Task TheBalances_NeverTouchTheGuests_AndAddTheCollectedPaymentsInSql()
    {
        var statements = new List<string>();
        await using var db = NpgsqlTranslationProbe.NewContext(statements);

        await NpgsqlTranslationProbe.AssertTranslatesAsync(() => Calls["balances-to-collect"](db, ScopeOf("whole-org")));

        // A money report carries no guest: no statement of it reads the table of the guests, so no name or contact can reach a row.
        Assert.All(statements, sql => Assert.DoesNotContain("\"Guests\"", sql, StringComparison.Ordinal));
        // What a stay has collected is added up by the database, not read payment by payment.
        Assert.Contains(statements, sql => sql.Contains("SUM(", StringComparison.OrdinalIgnoreCase) && sql.Contains("\"Payments\"", StringComparison.Ordinal));
    }
}
