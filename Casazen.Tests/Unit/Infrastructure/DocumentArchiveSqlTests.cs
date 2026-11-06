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

    /// <summary>The tables the archive may read, and the ones its filters and its reach join: the host's own, never a guest's.</summary>
    private static readonly string[] TablesOfTheArchive =
    [
        "PropertyDocuments", "LeaseDocuments", "TouristTaxRemittances", "Properties", "LeaseContracts", "PropertyMemberAccesses",
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
        ];
        return (new DocumentArchiveService(sources, db, Options.Create(new DocumentArchiveOptions()), NullLogger<DocumentArchiveService>.Instance), statements);
    }

    private static string[] Branches(string statement) => statement.Split("UNION ALL", StringSplitOptions.None);

    /// <summary>The branch of the union that reads the table of a source in its FROM (the tables it joins are read inside the others too).</summary>
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
        // The order and the page are in the statement, not made after it.
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

        // The org is a parameter in the WHERE of the branch, and of every property it joins; the tenant filter of the context is next to it.
        foreach (var (statement, table) in new[]
                 {
                     (wholeOrg, "PropertyDocuments"), (wholeOrg, "LeaseDocuments"), (wholeOrg, "TouristTaxRemittances"),
                     (limited, "PropertyDocuments"), (limited, "LeaseDocuments"),
                 })
        {
            Assert.Matches("\"OrgId\" = @", BranchOf(statement, table));
            Assert.Contains("@ef_filter", BranchOf(statement, table), StringComparison.Ordinal);
        }

        // A file names the org on itself, on its lease and on the property it joins; a property document on itself and on its property.
        Assert.Equal(3, Regex.Matches(BranchOf(wholeOrg, "LeaseDocuments"), "\"OrgId\" = @").Count);
        Assert.Equal(2, Regex.Matches(BranchOf(wholeOrg, "PropertyDocuments"), "\"OrgId\" = @").Count);

        // The whole org adds nothing to the branches: no grants, nothing cut out.
        Assert.DoesNotContain("PropertyMemberAccesses", wholeOrg, StringComparison.Ordinal);
        Assert.DoesNotContain("FALSE", BranchOf(wholeOrg, "TouristTaxRemittances"), StringComparison.OrdinalIgnoreCase);

        // A collaborator: the grants in the branches of the properties, and nothing at all from the receipts.
        Assert.Contains("EXISTS", BranchOf(limited, "PropertyDocuments"), StringComparison.Ordinal);
        Assert.Contains("PropertyMemberAccesses", BranchOf(limited, "PropertyDocuments"), StringComparison.Ordinal);
        Assert.Contains("PropertyMemberAccesses", BranchOf(limited, "LeaseDocuments"), StringComparison.Ordinal);
        Assert.DoesNotContain("PropertyMemberAccesses", BranchOf(limited, "TouristTaxRemittances"), StringComparison.Ordinal);
        Assert.Contains("FALSE", BranchOf(limited, "TouristTaxRemittances"), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TheDayOfAnUpload_IsWorkedOutByTheServer_InRome()
    {
        var statements = await ListAsync(OrgWide, Query(from: new DateOnly(2026, 1, 1)));

        // The day the host sees: the instant moved to Europe/Rome by the server, then cut to a date, in the branches of the two uploads.
        foreach (var table in new[] { "PropertyDocuments", "LeaseDocuments" })
            Assert.Contains("AT TIME ZONE 'Europe/Rome'", BranchOf(statements[1], table), StringComparison.Ordinal);

        // The filter by days is on that day, in each branch, not made on the rows after the statement.
        Assert.Contains("@", BranchOf(statements[1], "PropertyDocuments"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheSearch_IsAnILikeOnTheTitleThePropertyAndTheBookingCode_WithTheWildcardsOfTheTextEscaped()
    {
        var statements = await ListAsync(OrgWide, Query("50%_off"));

        Assert.All(statements, sql => Assert.Contains("ILIKE", sql, StringComparison.Ordinal));
        // The text is a parameter, never part of the statement.
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

        // One source of that type: one statement, with the org and the grants of the collaborator, and no other table's rows.
        var statement = Assert.Single(statements);
        Assert.Matches("\"OrgId\" = @", statement);
        Assert.Contains("PropertyMemberAccesses", statement, StringComparison.Ordinal);
        Assert.DoesNotContain("LeaseDocuments", statement, StringComparison.Ordinal);
    }

    /// <summary>
    /// PROOF TEST: privacy rule. The archive reads the tables of the host's documents and nothing that belongs to a guest: across the list, the
    /// count, the export and the download, every table the statements read is in the short list of the archive. The identity documents and the
    /// selfies of the guests live in other tables and are not reachable from any source; a source added later has to be added to the list of this
    /// test too, on purpose.
    /// </summary>
    [Fact]
    public async Task DocumentArchive_NoGuestIdentityDocuments_Test()
    {
        var (service, statements) = Build(count: 1);
        foreach (var scope in new[] { OrgWide, Restricted })
        {
            await NpgsqlTranslationProbe.AssertTranslatesAsync(() => service.ListAsync(scope, DocumentArchiveWorld.Everything, Query("x")));
            await NpgsqlTranslationProbe.AssertTranslatesAsync(() => service.CountByTypeAsync(scope, DocumentArchiveWorld.Everything, Query()));
            foreach (var type in new[] { ArchiveDocumentType.PropertyDocument, ArchiveDocumentType.Contract, ArchiveDocumentType.TouristTax })
                await NpgsqlTranslationProbe.AssertTranslatesAsync(() => service.OpenAsync(scope, DocumentArchiveWorld.Everything, type, Guid.NewGuid()));
        }

        Assert.NotEmpty(statements);
        var tables = statements
            .SelectMany(sql => Regex.Matches(sql, "(?:FROM|JOIN)\\s+\"(\\w+)\"").Select(match => match.Groups[1].Value))
            .ToHashSet(StringComparer.Ordinal);

        Assert.Subset(TablesOfTheArchive.ToHashSet(StringComparer.Ordinal), tables);
        // And, said the other way, the three sources are all there.
        Assert.Contains("PropertyDocuments", tables);
        Assert.Contains("LeaseDocuments", tables);
        Assert.Contains("TouristTaxRemittances", tables);
        Assert.All(statements, sql =>
        {
            foreach (var forbidden in new[] { "\"Guests\"", "\"StayGuests\"", "DocumentScanUrl", "DocumentImage", "Selfie" })
                Assert.DoesNotContain(forbidden, sql, StringComparison.OrdinalIgnoreCase);
        });
    }
}
