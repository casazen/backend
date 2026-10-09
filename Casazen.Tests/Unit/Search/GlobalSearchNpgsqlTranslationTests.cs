using System.Globalization;
using System.Text.RegularExpressions;
using Casazen.Core.Authorization;
using Casazen.Core.Search;
using Casazen.Infrastructure.Search;
using Casazen.Tests.Unit.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Xunit.Abstractions;

namespace Casazen.Tests.Unit.Search;

/// <summary>
/// UI-13a: every group of the global search, run on the PostgreSQL provider without a server (<see cref="NpgsqlTranslationProbe"/>),
/// with the three kinds of scope. In-memory evaluates a query the way .NET does and accepts what Npgsql cannot turn into SQL; this
/// fails here, before CI, when a query has no translation, and it reads the SQL that would be sent: the full-text predicate
/// is the expression of the index, the org and the scope are in the statement, and nothing is read that the answer does not show.
/// </summary>
public class GlobalSearchNpgsqlTranslationTests(ITestOutputHelper output)
{
    private static readonly Guid OrgId = Guid.Parse("7a3c1d52-9f0e-4b8a-8d65-2c4e1f0b9a11");
    private static readonly Guid SupplierOrgId = Guid.Parse("0b6d6a3e-3d1f-4f0a-9d7e-5b0c4f2a1e22");

    private static HostScope ScopeOf(string kind) => kind switch
    {
        "collaborator" => new HostScope(OrgId, GrantedToUserId: "auth0|collaboratore"),
        "account-in-no-team" => new HostScope(OrgId, OwnerId: "auth0|titolare"),
        _ => new HostScope(OrgId),
    };

    public static TheoryData<string> Scopes => ["collaborator", "account-in-no-team", "whole-org"];

    private static GlobalSearchRequest Request(string term, HostSearchAccess? host, Guid? supplierOrgId = null) =>
        new(SearchText.Parse(term), SearchLimits.DefaultLimit, host, supplierOrgId, CultureInfo.GetCultureInfo("it-IT"));

    private static HostSearchAccess EverythingOf(string scope) => new(ScopeOf(scope), true, true, true, true, true);

    private async Task<List<string>> RunAsync(GlobalSearchRequest request)
    {
        var statements = new List<string>();
        await using var db = NpgsqlTranslationProbe.NewContext(statements);

        await NpgsqlTranslationProbe.AssertTranslatesAsync(
            () => new GlobalSearchService(db, NullLogger<GlobalSearchService>.Instance).SearchAsync(request));

        foreach (var statement in statements)
            output.WriteLine(statement + "\n");
        return statements;
    }

    [Theory]
    [MemberData(nameof(Scopes))]
    public async Task Search_EveryGroupOfAHost_IsTranslatedToSqlByNpgsql(string scope)
    {
        var statements = await RunAsync(Request("rossi mar", EverythingOf(scope)));

        // properties, bookings by guest, guests, leases, host requests, suppliers
        Assert.Equal(6, statements.Count);
    }

    [Fact]
    public async Task Search_ABookingCode_AddsTheQueryByCode()
    {
        var statements = await RunAsync(Request("7k3m9-pq2xv", EverythingOf("whole-org")));

        Assert.Equal(7, statements.Count);
    }

    [Fact]
    public async Task Search_TheSupplierInbox_IsTranslatedToSqlByNpgsql()
    {
        var statements = await RunAsync(Request("pulizia", null, SupplierOrgId));

        Assert.Single(statements);
    }
}
