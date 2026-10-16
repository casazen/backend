using System.Text.RegularExpressions;
using Casazen.Core.Authorization;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Services;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Casazen.Tests.Unit.Infrastructure;

/// <summary>
/// AM-02c: the SQL the overview of the home sends on the real provider (Npgsql, no server: <see cref="NpgsqlTranslationProbe"/>).
/// What InMemory cannot prove is that each query of the service is <b>translated</b> (the grouped count of the properties, with
/// a computed key; the <c>NOT EXISTS</c> on the credentials; the filter of the people on the activity) and that every figure is
/// one statement: a count or a grouped count, never a query per property or per person. The values are asserted in
/// <c>OrgOverviewServiceTests</c> (InMemory) and the number of commands against the size of the org in
/// <c>OrgTeamCompletionsPostgresTests</c> (CI).
/// </summary>
public class OrgOverviewSqlShapeTests
{
    private const string Collaborator = "auth0|collaboratore";

    private static readonly Guid OrgId = Guid.Parse("7a3c1d52-9f0e-4b8a-8d65-2c4e1f0b9a11");
    private static readonly HostScope OrgWide = new(OrgId);
    private static readonly HostScope OfOneMode = new(OrgId, GrantedToUserId: Collaborator, GrantedMode: RentalMode.Short);

    private static OrgOverviewService NewService(AppDbContext db)
    {
        var entitlement = new EntitlementService(db, new ConfigurationBuilder().Build());
        return new OrgOverviewService(db, entitlement, new OrgSeatService(db, entitlement, TimeProvider.System), new OrgActivityService(db));
    }

    /// <summary>The statements the call sent to the (absent) server. A query that cannot be translated fails here.</summary>
    private static async Task<List<string>> StatementsOfAsync(Func<OrgOverviewService, Task> call)
    {
        var statements = new List<string>();
        await using var db = NpgsqlTranslationProbe.NewContext(statements);
        await NpgsqlTranslationProbe.AssertTranslatesAsync(() => call(NewService(db)));
        return statements;
    }

    private static int Count(string text, string part) => text.Split(part, StringSplitOptions.None).Length - 1;

    [Fact]
    public async Task Properties_AreOneGroupedStatement_WithTheReachOfTheCallerInside()
    {
        var statements = await StatementsOfAsync(service => service.PropertiesAsync(OrgId, OfOneMode, CancellationToken.None));

        var sql = Assert.Single(statements);
        Assert.Contains("GROUP BY", sql, StringComparison.Ordinal);
        Assert.Contains("\"RentalMode\"", sql, StringComparison.Ordinal);
        // «To complete» is the compliance status that already says it, computed in the same statement.
        Assert.Contains("\"ComplianceStatus\"", sql, StringComparison.Ordinal);
        // One EXISTS on the grants of the person, and the mode it works in as a parameter next to it: no list of ids.
        Assert.Equal(1, Count(sql, "EXISTS ("));
        Assert.Equal(1, Count(sql, "\"PropertyMemberAccesses\""));
        Assert.Matches("\"RentalMode\" = @", sql);
        Assert.DoesNotContain(Collaborator, sql, StringComparison.Ordinal);
        Assert.DoesNotContain(" IN (", sql, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Properties_OfAnOrgWideCaller_HaveNoScopePredicateAtAll()
    {
        var statements = await StatementsOfAsync(service => service.PropertiesAsync(OrgId, OrgWide, CancellationToken.None));

        var sql = Assert.Single(statements);
        Assert.Contains("GROUP BY", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("PropertyMemberAccesses", sql, StringComparison.Ordinal);
        Assert.Equal(0, Count(sql, "EXISTS ("));
    }

    [Fact]
    public async Task Alloggiati_AreOneCount_WithANotExistsOnTheCredentials_InsideTheReachOfTheCaller()
    {
        var statements = await StatementsOfAsync(service => service.AlloggiatiAsync(OrgId, OfOneMode, CancellationToken.None));

        var sql = Assert.Single(statements);
        Assert.Contains("\"PropertyQuesturaCredentials\"", sql, StringComparison.Ordinal);
        Assert.Matches(new Regex(@"NOT \(?EXISTS", RegexOptions.CultureInvariant), sql);
        // The scope is a second EXISTS, on the grants, in the same statement: two in all, and no query of its own.
        Assert.Equal(2, Count(sql, "EXISTS ("));
        Assert.Equal(1, Count(sql, "\"PropertyMemberAccesses\""));
    }

    [Fact]
    public async Task Invitations_AreCountedInOneStatement_OfTheOrgAndOnlyOfTheOpenOnes()
    {
        var statements = await StatementsOfAsync(service => service.InvitationsAsync(OrgId, DateTime.UtcNow, CancellationToken.None));

        // The count answers 0 here, so the date of the oldest is not asked: one statement for a org with nothing waiting.
        var sql = Assert.Single(statements);
        Assert.Contains("\"OrgInvitations\"", sql, StringComparison.Ordinal);
        Assert.Contains("\"ExpiresAt\"", sql, StringComparison.Ordinal);
        Assert.Contains("\"Status\"", sql, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Standing_IsOneSelectOfTheStatusOfTheSubscription()
    {
        var statements = await StatementsOfAsync(service => service.StandingAsync(OrgId, CancellationToken.None));

        var sql = Assert.Single(statements);
        Assert.Contains("\"SubscriptionStatus\"", sql, StringComparison.Ordinal);
        Assert.Contains("FROM \"Orgs\"", sql, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RecentActivity_TheFilterOfThePeople_IsInTheStatementsThatListTheLines()
    {
        var statements = new List<string>();
        await using var db = NpgsqlTranslationProbe.NewContext(statements);

        await NpgsqlTranslationProbe.AssertTranslatesAsync(() => new OrgActivityService(db).ListAsync(
            OrgId, new OrgActivityFilter(PeopleOnly: true), page: 1, pageSize: OrgOverviewService.RecentActivityCount, CancellationToken.None));

        // Whatever the statements are (the count, then the page), every one of them leaves the jobs and the webhooks out.
        Assert.NotEmpty(statements);
        Assert.All(statements, sql => Assert.Contains("\"ActorUserId\" IS NOT NULL", sql, StringComparison.Ordinal));
    }
}
