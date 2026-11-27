using System.Text.RegularExpressions;
using Casazen.Core.Authorization;
using Casazen.Infrastructure.Services;
using Casazen.Tests.Unit.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Casazen.Tests.Unit.Infrastructure;

/// <summary>
/// The list of the owners of an agency (FIX/RES-OWNERS) on the PostgreSQL provider with no server (see <see cref="NpgsqlTranslationProbe"/>). InMemory evaluates a query the
/// way .NET does and accepts what Npgsql cannot turn into SQL: the list is one projection with six correlated sub-queries (the properties held, the fiscal profile in force, the
/// mandate in force), and, for a caller with a partial reach, a sub-query of the reachable properties inside an <c>EXISTS</c> on the periods of the owner. A query that has no
/// translation fails here, before the CI, and not as a 500 of the page «Proprietari»; the statement is read for the org on every table and for the columns the list needs.
/// </summary>
public class OwnerDirectoryNpgsqlTranslationTests
{
    private static readonly Guid OrgId = Guid.Parse("7a3c1d52-9f0e-4b8a-8d65-2c4e1f0b9a11");
    private static readonly Casazen.Tests.Unit.FakeTimeProvider Clock = new(HostScopeScenario.Now);

    // The org as a parameter of the statement, never a literal: EF names it @orgId, or after the closure that holds it (@_8__locals1_orgId) inside a nested lambda.
    private static readonly Regex OrgPredicate = new(@"""OrgId"" = @\w*orgId\b", RegexOptions.Compiled);

    public static TheoryData<string> Scopes => ["collaborator", "account-in-no-team", "whole-org"];

    private static HostScope ScopeOf(string kind) => kind switch
    {
        "collaborator" => new HostScope(OrgId, GrantedToUserId: "auth0|collaboratore"),
        "account-in-no-team" => new HostScope(OrgId, OwnerId: "auth0|titolare"),
        _ => new HostScope(OrgId),
    };

    [Theory]
    [MemberData(nameof(Scopes))]
    public async Task OwnersList_IsOneStatementTranslatedToSqlByNpgsql_WithTheOrgOnEveryTable_Test(string scopeKind)
    {
        var statements = new List<string>();
        await using var db = NpgsqlTranslationProbe.NewContext(statements);
        var service = new OwnerDirectoryService(db, Clock, NullLogger<OwnerDirectoryService>.Instance);

        await NpgsqlTranslationProbe.AssertTranslatesAsync(() => service.ListAsync(ScopeOf(scopeKind)));

        // One statement for the whole list: the facts of each owner are read with it, never one read for each owner.
        var sql = Assert.Single(statements);
        Assert.Contains("FROM \"PropertyOwners\"", sql, StringComparison.Ordinal);
        Assert.Contains("\"PropertyOwnerships\"", sql, StringComparison.Ordinal);
        Assert.Contains("\"OwnerFiscalProfiles\"", sql, StringComparison.Ordinal);
        Assert.Contains("\"OwnerMandates\"", sql, StringComparison.Ordinal);
        Assert.Contains("ORDER BY", sql, StringComparison.OrdinalIgnoreCase);

        // The org on the owners, on the periods and the properties of the count, on the two sub-queries of the profile and the six of the mandate; and, for a partial reach,
        // on the properties it reaches and on the two checks on the periods of the owner.
        var expectedOrgChecks = scopeKind == "whole-org" ? 11 : 14;
        Assert.True(OrgPredicate.Matches(sql).Count >= expectedOrgChecks, $"the org is not asked of every table ({OrgPredicate.Matches(sql).Count} of {expectedOrgChecks}):{Environment.NewLine}{sql}");

        // A partial reach is the grants of the caller (a collaborator) or the creator of the property (an account outside the team), inside the same statement.
        if (scopeKind == "collaborator")
            Assert.Contains("\"PropertyMemberAccesses\"", sql, StringComparison.Ordinal);
        if (scopeKind == "account-in-no-team")
            Assert.Contains("\"OwnerId\" = @", sql, StringComparison.Ordinal);
        if (scopeKind == "whole-org")
            Assert.DoesNotContain("\"PropertyMemberAccesses\"", sql, StringComparison.Ordinal);

        // The list reads the columns it shows: no identifier, address or bank account of the owner nor of its fiscal profile is in the statement.
        foreach (var column in new[] { "FiscalCode", "VatNumber", "\"Bic\"", "BillingStreet", "BillingCity", "LegalAddress", "BankAccountReference", "ForeignTaxId", "MandatePdfUrl" })
            Assert.DoesNotContain(column, sql, StringComparison.Ordinal);
    }
}
