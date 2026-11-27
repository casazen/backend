using System.Data;
using System.Text.RegularExpressions;
using Casazen.Core.Entities;
using Casazen.Core.Features;
using Casazen.Core.Services;
using Casazen.Infrastructure.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Infrastructure;

/// <summary>
/// E20/F02/US01/T02 (NF-121): the SQL the deletion of the guests' documents becomes on the real PostgreSQL provider (Npgsql; nothing is executed, see
/// <see cref="NpgsqlTranslationProbe"/>). InMemory evaluates a query the way .NET does and accepts what Npgsql cannot turn into SQL, and the job runs where nobody sees a
/// translation error until the night it fails: this is where it is proved that every query of one run translates, and that each of them names the org of the run.
/// </summary>
public class GuestDocumentPurgeNpgsqlTranslationTests
{
    private static readonly Guid OrgId = Guid.Parse("7a3c1d52-9f0e-4b8a-8d65-2c4e1f0b9a11");
    private static readonly Guid PropertyId = Guid.Parse("2b6c8f10-3d4e-4a59-9c7b-1e0d2f3a4b55");
    private static readonly Guid GuestId = Guid.Parse("c3d2e1f0-a9b8-4c7d-8e6f-5a4b3c2d1e0f");
    private static readonly Guid StayId = Guid.Parse("9e8d7c6b-5a49-4382-b1a0-f9e8d7c6b5a4");
    private static readonly Guid ProofId = Guid.Parse("5d4c3b2a-1f0e-4d9c-8b7a-6f5e4d3c2b1a");

    private static readonly DateTime Arrival = new(2026, 11, 3, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>
    /// PROOF TEST: data and permission rule (shape of the statements). A run for one org reads, in order: the stays that owe the communication and hold a document (the org,
    /// the active statuses, no no-show mark, a photo or a scan of the booker), the receipts that cover them (the org, the flats and the days), what the covered stay holds
    /// (its photos, the reference of the scan of its booker, the other stays of that guest) and the record of the guest; every one of them translates, and none of them is left
    /// to the tenant filter of the context (a job has none) but names the org itself.
    /// </summary>
    [Fact]
    public async Task GuestDocumentPurge_EveryQueryOfARun_TranslatesAndNamesTheOrg_Test()
    {
        var statements = new List<string>();
        var db = NpgsqlTranslationProbe.NewContext(statements, count: 0, Rows);
        var storage = new Mock<IFileStorage>();
        var flags = new Mock<IFeatureFlags>();
        flags.Setup(f => f.IsEnabled(FeatureFlags.PurgeDocumentsAfterReceipt)).Returns(true);

        // The traffic light reads every stay it is asked about green: the statements are the ones of the purge, not the state engine's (which has its own proofs).
        var engine = new Mock<IComplianceStatusService>();
        engine
            .Setup(s => s.GetStaysAReceiptTurnsGreenAsync(It.IsAny<Guid>(), It.IsAny<IReadOnlyCollection<Guid>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid _, IReadOnlyCollection<Guid> ids, CancellationToken _) => ids.ToHashSet());
        var service = new GuestDocumentPurgeService(
            db,
            storage.Object,
            new GuestDataEraser(db, storage.Object, NullLogger<GuestDataEraser>.Instance),
            engine.Object,
            flags.Object,
            new FakeTimeProvider(new DateTimeOffset(2026, 11, 6, 10, 0, 0, TimeSpan.Zero)),
            NullLogger<GuestDocumentPurgeService>.Instance);

        await NpgsqlTranslationProbe.AssertTranslatesAsync(() => service.PurgeAsync(OrgId, CancellationToken.None));

        var stays = Assert.Single(statements, sql => Root(sql) == "Bookings" && sql.Contains("\"DocumentImages\"", StringComparison.Ordinal));
        Assert.Matches("\"OrgId\" = @", stays);
        Assert.Matches("\"Status\" IN \\(", stays);
        Assert.Contains("\"NoShowAt\" IS NULL", stays, StringComparison.Ordinal);
        Assert.Contains("EXISTS", stays, StringComparison.Ordinal);
        Assert.Contains("\"DocumentScanUrl\" IS NOT NULL", stays, StringComparison.Ordinal);
        Assert.Matches("ORDER BY .*\"CheckInDate\"", stays);

        // The receipts that cover the stays: the flat the proof was filed under and the flats it also covers, each of the org.
        var filed = Assert.Single(statements, sql => Root(sql) == "ManualComplianceProofs");
        var covered = Assert.Single(statements, sql => Root(sql) == "ManualComplianceProofProperties");
        Assert.All(new[] { filed, covered }, sql =>
        {
            Assert.Matches("\"OrgId\" = @", sql);
            Assert.Matches("\"Day\" >= @", sql);
            Assert.Matches("\"Day\" <= @", sql);
        });

        // What the covered stay holds: its photos, the reference of the scan of its booker, the other stays of that guest, then the record of the guest.
        var photos = Assert.Single(statements, sql => Root(sql) == "DocumentImages");
        Assert.Matches("\"OrgId\" = @", photos);
        Assert.Matches("\"BookingId\" = @", photos);
        var guestStatements = statements.Where(sql => Root(sql) == "Guests").ToList();
        Assert.Equal(2, guestStatements.Count);
        Assert.All(guestStatements, sql =>
        {
            Assert.Matches("\"OrgId\" = @", sql);
            Assert.Matches("\"Id\" = @", sql);
        });
        var others = Assert.Single(statements, sql => Root(sql) == "Bookings" && !sql.Contains("\"DocumentImages\"", StringComparison.Ordinal));
        Assert.Matches("\"GuestId\" = @", others);
        Assert.Matches("\"OrgId\" = @", others);
        Assert.Matches("\"Id\" <> @", others);
        Assert.Matches("\"Status\" IN \\(", others);
        Assert.Contains("\"NoShowAt\" IS NULL", others, StringComparison.Ordinal);

        // A job has no tenant: no statement of the run leans on the filter of the context, which is off there; each one names the org itself. The reading of the receipts
        // is the one of the traffic light (it names the org too, and keeps the filter of the context, which a request relies on).
        Assert.DoesNotContain(
            statements.Where(sql => Root(sql) is not ("ManualComplianceProofs" or "ManualComplianceProofProperties")),
            sql => sql.Contains("@ef_filter", StringComparison.Ordinal));
    }

    /// <summary>The table a statement reads first (the root of its query): its subqueries read others, and do not tell what the statement is.</summary>
    private static string Root(string sql) => Regex.Match(sql, "FROM \"(\\w+)\"").Groups[1].Value;

    /// <summary>One row for each shape the run reads, so the queries that come after the first are compiled too.</summary>
    private static DataTable? Rows(string sql)
    {
        if (Regex.IsMatch(sql, "FROM \"Bookings\"", RegexOptions.IgnoreCase) && sql.Contains("\"DocumentImages\"", StringComparison.Ordinal))
        {
            return Table(
                [("Id", typeof(Guid)), ("OrgId", typeof(Guid)), ("PropertyId", typeof(Guid)), ("GuestId", typeof(Guid)), ("CheckInDate", typeof(DateTime))],
                StayId, OrgId, PropertyId, GuestId, Arrival);
        }

        if (Regex.IsMatch(sql, "FROM \"ManualComplianceProofs\"", RegexOptions.IgnoreCase))
        {
            return Table(
                [("Id", typeof(Guid)), ("PropertyId", typeof(Guid)), ("Kind", typeof(int)), ("Day", typeof(DateOnly)), ("UploadedAt", typeof(DateTime))],
                ProofId, PropertyId, (int)ManualProofKind.AlloggiatiReceipt, new DateOnly(2026, 11, 3), Arrival);
        }

        // The reference of the scan of the booker (one column); the whole record of the guest is left empty.
        if (Regex.IsMatch(sql, "FROM \"Guests\"", RegexOptions.IgnoreCase) && !sql.Contains("\"FirstName\"", StringComparison.Ordinal))
            return Table([("DocumentScanUrl", typeof(string))], $"guest-documents/{OrgId}/{GuestId}/scansione.jpg");

        return null;
    }

    private static DataTable Table((string Name, Type Type)[] columns, params object[] values)
    {
        var table = new DataTable();
        foreach (var (name, type) in columns)
            table.Columns.Add(name, type);

        table.Rows.Add(values);
        return table;
    }
}
