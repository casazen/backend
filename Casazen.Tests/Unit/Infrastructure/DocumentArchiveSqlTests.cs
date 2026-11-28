using System.Data;
using System.Text.RegularExpressions;
using Casazen.Core.Authorization;
using Casazen.Core.Options;
using Casazen.Core.Services;
using Casazen.Infrastructure.Services;
using Casazen.Tests.Unit.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Casazen.Tests.Unit.Infrastructure;

/// <summary>
/// NF-99, E07/F03/US03: the SQL the document archive becomes on the real PostgreSQL provider (Npgsql; nothing is executed, see
/// <see cref="NpgsqlTranslationProbe"/>). InMemory evaluates a query the way .NET does and accepts what Npgsql cannot turn into SQL, so this is where
/// the claims of the brief about the statement are proved: one <c>UNION ALL</c> whatever the number of sources, filters and paging in the
/// statement, the org in every branch, the reach of the caller in the statement, the day worked out by the server in Rome, and no table that
/// belongs to the guests. The data-level proofs are in <c>DocumentArchiveServiceTests</c> (InMemory) and <c>DocumentArchivePostgresTests</c> (CI).
/// </summary>
public class DocumentArchiveSqlTests
{
    private static readonly Guid OrgId = Guid.Parse("7a3c1d52-9f0e-4b8a-8d65-2c4e1f0b9a11");
    private static readonly HostScope OrgWide = new(OrgId);
    private static readonly HostScope Restricted = new(OrgId, GrantedToUserId: "auth0|collaboratore");

    private static readonly string[] TablesOfTheArchive =
    [
        "PropertyDocuments", "LeaseDocuments", "TouristTaxRemittances", "Properties", "LeaseContracts", "PropertyMemberAccesses",
        "ManualComplianceProofs", "ManualComplianceProofProperties",
    ];

    private static DocumentArchiveQuery Query(
        string? search = null, DateOnly? from = null, DateOnly? to = null, Guid? propertyId = null, Guid? bookingId = null) =>
        new(null, from, to, propertyId, bookingId, search, 2, 10);

    private static (DocumentArchiveService Service, List<string> Statements) Build(int count = 1, Func<string, DataTable?>? rows = null)
    {
        var statements = new List<string>();
        var db = NpgsqlTranslationProbe.NewContext(statements, count, rows);
        var storage = new InMemoryFileStorage();
        IDocumentArchiveSource[] sources =
        [
            new PropertyDocumentArchiveSource(db, storage),
            new LeaseDocumentArchiveSource(db, storage),
            new TouristTaxRemittanceArchiveSource(db, storage),
            new AlloggiatiReceiptArchiveSource(db, storage),
            new IstatProofArchiveSource(db, storage),
        ];
        return (new DocumentArchiveService(sources, db, Options.Create(new DocumentArchiveOptions()), NullLogger<DocumentArchiveService>.Instance), statements);
    }

    private static string[] Branches(string statement) => statement.Split("UNION ALL", StringSplitOptions.None);

    private static string BranchOf(string statement, string table) =>
        Assert.Single(Branches(statement), branch => Regex.IsMatch(branch, $"FROM \"{table}\""));

    private static async Task<List<string>> ListAsync(HostScope scope, DocumentArchiveQuery query)
    {
        var (service, statements) = Build();
        await NpgsqlTranslationProbe.AssertTranslatesAsync(
            () => service.ListAsync(scope, DocumentArchiveWorld.Everything, query));
        return statements;
    }

    [Fact]
    public async Task List_IsTheCountAndThePage_BothOverOneUnionAllOfTheThreeSources_WhateverTheFilters()
    {
        var statements = await ListAsync(
            OrgWide, Query("50%_off", new DateOnly(2026, 1, 1), new DateOnly(2026, 12, 31), Guid.NewGuid(), Guid.NewGuid()));

        Assert.Equal(2, statements.Count);
        Assert.StartsWith("SELECT count(*)", statements[0].TrimStart(), StringComparison.OrdinalIgnoreCase);
        Assert.All(statements, sql => Assert.Equal(2, Regex.Matches(sql, "UNION ALL").Count));
        Assert.Matches("ORDER BY .*\"DocumentDate\" DESC, .*\"Id\"", statements[1]);
        Assert.Contains("LIMIT", statements[1], StringComparison.Ordinal);
        Assert.Contains("OFFSET", statements[1], StringComparison.Ordinal);
    }

    /// <summary>
    /// PROOF TEST: data rule (shape of the statement). Every branch of the statement names the org of the scope on its own table, whatever the
    /// scope, on top of the tenant filter of the context; the two sources that belong to a property are narrowed by the grants of a collaborator
    /// «Solo alcuni» with an EXISTS in the same statement, and the receipts, which have no property, are cut out of a limited scope in SQL.
    /// </summary>
    [Fact]
    public async Task DocumentArchive_TenantAndScopeIsolation_Sql_Test()
    {
        var wholeOrg = (await ListAsync(OrgWide, Query()))[1];
        var limited = (await ListAsync(Restricted, Query()))[1];

        foreach (var (statement, table) in new[]
                 {
                     (wholeOrg, "PropertyDocuments"), (wholeOrg, "LeaseDocuments"), (wholeOrg, "TouristTaxRemittances"),
                     (limited, "PropertyDocuments"), (limited, "LeaseDocuments"),
                 })
        {
            Assert.Matches("\"OrgId\" = @", BranchOf(statement, table));
            Assert.Contains("@ef_filter", BranchOf(statement, table), StringComparison.Ordinal);
        }

        Assert.Equal(3, Regex.Matches(BranchOf(wholeOrg, "LeaseDocuments"), "\"OrgId\" = @").Count);
        Assert.Equal(2, Regex.Matches(BranchOf(wholeOrg, "PropertyDocuments"), "\"OrgId\" = @").Count);

        Assert.DoesNotContain("PropertyMemberAccesses", wholeOrg, StringComparison.Ordinal);
        Assert.DoesNotContain("FALSE", BranchOf(wholeOrg, "TouristTaxRemittances"), StringComparison.OrdinalIgnoreCase);

        Assert.Contains("EXISTS", BranchOf(limited, "PropertyDocuments"), StringComparison.Ordinal);
        Assert.Contains("PropertyMemberAccesses", BranchOf(limited, "PropertyDocuments"), StringComparison.Ordinal);
        Assert.Contains("PropertyMemberAccesses", BranchOf(limited, "LeaseDocuments"), StringComparison.Ordinal);
        Assert.DoesNotContain("PropertyMemberAccesses", BranchOf(limited, "TouristTaxRemittances"), StringComparison.Ordinal);
        Assert.Contains("FALSE", BranchOf(limited, "TouristTaxRemittances"), StringComparison.OrdinalIgnoreCase);
    }

    private static string[] BranchesOf(string statement, string table) =>
        [.. Branches(statement).Where(branch => Regex.IsMatch(branch, $"FROM \"{table}\""))];

    /// <summary>
    /// PROOF TEST: data rule (shape of the statement, E20/F02/US01/T01). With the proofs of the compliance duties asked for, the list is ONE union of the five
    /// sources, and no branch lifts the soft-delete filter of the model: the statement is one, so a filter lifted by one source would be lifted for all of them and the
    /// documents of a deleted property would come back. Every branch that joins a property carries its <c>IsDeleted</c> test, the tenant filter stays in all five, and a
    /// collaborator reads a receipt of several flats only when every flat of it is in its reach (a NOT EXISTS in the same statement).
    /// </summary>
    [Fact]
    public async Task DocumentArchive_TheFiveSourcesAreOneStatement_AndNoBranchLiftsTheSoftDeleteFilter_Sql_Test()
    {
        async Task<List<string>> ListWithProofsAsync(HostScope scope)
        {
            var (service, statements) = Build();
            await NpgsqlTranslationProbe.AssertTranslatesAsync(() => service.ListAsync(scope, DocumentArchiveWorld.EveryType, Query()));
            return statements;
        }

        var wholeOrg = await ListWithProofsAsync(OrgWide);
        var limited = await ListWithProofsAsync(Restricted);

        Assert.Equal(2, wholeOrg.Count);
        Assert.Equal(2, limited.Count);
        Assert.All(wholeOrg.Concat(limited), statement => Assert.Equal(4, Regex.Matches(statement, "UNION ALL").Count));

        foreach (var statement in new[] { wholeOrg[1], limited[1] })
        {
            var withProperty = new[] { "PropertyDocuments", "LeaseDocuments", "ManualComplianceProofs" }
                .SelectMany(table => BranchesOf(statement, table))
                .ToList();
            Assert.Equal(4, withProperty.Count);
            Assert.All(withProperty, branch => Assert.Contains("\"IsDeleted\"", branch, StringComparison.Ordinal));

            Assert.Equal(5, Branches(statement).Length);
            Assert.All(withProperty, branch => Assert.Contains("@ef_filter", branch, StringComparison.Ordinal));
        }

        Assert.All(Branches(wholeOrg[1]), branch => Assert.Contains("@ef_filter", branch, StringComparison.Ordinal));

        Assert.All(BranchesOf(limited[1], "ManualComplianceProofs"), branch =>
        {
            Assert.Contains("PropertyMemberAccesses", branch, StringComparison.Ordinal);
            Assert.Contains("NOT EXISTS", branch, StringComparison.Ordinal);
            Assert.Contains("ManualComplianceProofProperties", branch, StringComparison.Ordinal);
            Assert.Matches("(?is)count\\(.*count\\(", branch);
        });
        Assert.All(BranchesOf(wholeOrg[1], "ManualComplianceProofs"), branch => Assert.DoesNotContain("count(", branch, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task TheDayOfAnUpload_IsWorkedOutByTheServer_InRome()
    {
        var statements = await ListAsync(OrgWide, Query(from: new DateOnly(2026, 1, 1)));

        foreach (var table in new[] { "PropertyDocuments", "LeaseDocuments" })
            Assert.Contains("AT TIME ZONE 'Europe/Rome'", BranchOf(statements[1], table), StringComparison.Ordinal);

        Assert.Contains("@", BranchOf(statements[1], "PropertyDocuments"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheSearch_IsAnILikeOnTheTitleThePropertyAndTheBookingCode_WithTheWildcardsOfTheTextEscaped()
    {
        var statements = await ListAsync(OrgWide, Query("50%_off"));

        Assert.All(statements, sql => Assert.Contains("ILIKE", sql, StringComparison.Ordinal));
        Assert.All(statements, sql => Assert.DoesNotContain("50%_off", sql, StringComparison.Ordinal));
    }

    [Fact]
    public async Task CountByType_IsOneStatement_GroupedByTheType_OverTheSameUnion()
    {
        var (service, statements) = Build();

        await NpgsqlTranslationProbe.AssertTranslatesAsync(
            () => service.CountByTypeAsync(OrgWide, DocumentArchiveWorld.Everything, Query(search: "x")));

        var statement = Assert.Single(statements);
        Assert.Contains("GROUP BY", statement, StringComparison.Ordinal);
        Assert.Equal(2, Regex.Matches(statement, "UNION ALL").Count);
    }

    [Fact]
    public async Task Export_CountsAndSumsFirst_ThenListsTheDocumentsOfTheSameUnion()
    {
        var (service, statements) = Build(
            count: 1,
            rows: sql =>
            {
                if (!Regex.IsMatch(sql.TrimStart(), @"^SELECT\s+(COALESCE\()?sum\(", RegexOptions.IgnoreCase))
                    return null;

                var table = new DataTable();
                table.Columns.Add("value", typeof(long));
                table.Rows.Add(0L);
                return table;
            });
        using var destination = new MemoryStream();

        await NpgsqlTranslationProbe.AssertTranslatesAsync(
            () => service.ExportAsync(OrgWide, DocumentArchiveWorld.Everything, Query(), destination));

        Assert.Equal(3, statements.Count);
        Assert.StartsWith("SELECT count(*)", statements[0].TrimStart(), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("sum(", statements[1], StringComparison.OrdinalIgnoreCase);
        Assert.Matches("ORDER BY", statements[2]);
        Assert.All(statements, sql => Assert.Equal(2, Regex.Matches(sql, "UNION ALL").Count));
    }

    [Fact]
    public async Task TheRowOfADownload_IsLookedUpWithTheQueryOfTheList_ForTheSourceOfTheType()
    {
        var (service, statements) = Build(count: 1);

        await NpgsqlTranslationProbe.AssertTranslatesAsync(
            () => service.OpenAsync(Restricted, DocumentArchiveWorld.Everything, ArchiveDocumentType.PropertyDocument, Guid.NewGuid()));

        var statement = Assert.Single(statements);
        Assert.Matches("\"OrgId\" = @", statement);
        Assert.Contains("PropertyMemberAccesses", statement, StringComparison.Ordinal);
        Assert.DoesNotContain("LeaseDocuments", statement, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DocumentArchive_NoGuestIdentityDocuments_Test()
    {
        var (service, statements) = Build(count: 1);
        foreach (var scope in new[] { OrgWide, Restricted })
        {
            await NpgsqlTranslationProbe.AssertTranslatesAsync(() => service.ListAsync(scope, DocumentArchiveWorld.EveryType, Query("x")));
            await NpgsqlTranslationProbe.AssertTranslatesAsync(() => service.CountByTypeAsync(scope, DocumentArchiveWorld.EveryType, Query()));
            foreach (var type in new[]
                     {
                         ArchiveDocumentType.PropertyDocument, ArchiveDocumentType.Contract, ArchiveDocumentType.TouristTax,
                         ArchiveDocumentType.AlloggiatiReceipt, ArchiveDocumentType.IstatProof,
                     })
            {
                await NpgsqlTranslationProbe.AssertTranslatesAsync(() => service.OpenAsync(scope, DocumentArchiveWorld.EveryType, type, Guid.NewGuid()));
            }
        }

        Assert.NotEmpty(statements);
        var tables = statements
            .SelectMany(sql => Regex.Matches(sql, "(?:FROM|JOIN)\\s+\"(\\w+)\"").Select(match => match.Groups[1].Value))
            .ToHashSet(StringComparer.Ordinal);

        Assert.Subset(TablesOfTheArchive.ToHashSet(StringComparer.Ordinal), tables);
        Assert.Contains("PropertyDocuments", tables);
        Assert.Contains("LeaseDocuments", tables);
        Assert.Contains("TouristTaxRemittances", tables);
        Assert.Contains("ManualComplianceProofs", tables);
        Assert.All(statements, sql =>
        {
            foreach (var forbidden in new[] { "\"Guests\"", "\"StayGuests\"", "DocumentScanUrl", "DocumentImage", "Selfie" })
                Assert.DoesNotContain(forbidden, sql, StringComparison.OrdinalIgnoreCase);
        });
    }
}
