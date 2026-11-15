using System.Data;
using System.Text.RegularExpressions;
using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Enums;
using Casazen.Core.Features;
using Casazen.Core.Options;
using Casazen.Core.Services;
using Casazen.Infrastructure.Documents;
using Casazen.Infrastructure.Jobs;
using Casazen.Infrastructure.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Infrastructure;

/// <summary>
/// E03/F02/US03/T01 (NF-47): the SQL the proof archive becomes on the real PostgreSQL provider (Npgsql; nothing is executed, see
/// <see cref="NpgsqlTranslationProbe"/>). InMemory evaluates a query the way .NET does and accepts what Npgsql cannot turn into SQL, and the
/// integration tests of CI do not call these services: this is where it is proved that every query of the archive translates, and that the
/// org and the reach of the caller are in the statement, not made after it.
/// </summary>
public class ComplianceArchiveNpgsqlTranslationTests
{
    private static readonly Guid OrgId = Guid.Parse("7a3c1d52-9f0e-4b8a-8d65-2c4e1f0b9a11");
    private static readonly Guid PropertyId = Guid.Parse("2b6c8f10-3d4e-4a59-9c7b-1e0d2f3a4b55");
    private static readonly Guid OtherPropertyId = Guid.Parse("c3d2e1f0-a9b8-4c7d-8e6f-5a4b3c2d1e0f");
    private static readonly Guid BookingId = Guid.Parse("9e8d7c6b-5a49-4382-b1a0-f9e8d7c6b5a4");
    private static readonly Guid ProofId = Guid.Parse("5d4c3b2a-1f0e-4d9c-8b7a-6f5e4d3c2b1a");

    private static readonly HostScope OrgWide = new(OrgId);
    private static readonly HostScope Restricted = new(OrgId, GrantedToUserId: "auth0|collaboratore");
    private static readonly HostScope Owned = new(OrgId, OwnerId: "auth0|titolare");

    private static readonly DateTime Arrival = new(2026, 11, 3, 0, 0, 0, DateTimeKind.Utc);

    private static (ComplianceArchiveService Service, List<string> Statements) Build(
        Func<string, DataTable?>? rows = null, IComplianceStatusService? status = null)
    {
        var statements = new List<string>();
        var db = NpgsqlTranslationProbe.NewContext(statements, count: 1, rows ?? Rows);
        var flags = new Mock<IFeatureFlags>();
        flags.Setup(f => f.IsEnabled(FeatureFlags.ComplianceTrafficLight)).Returns(false);
        var service = new ComplianceArchiveService(
            db,
            new SigningStorage(),
            status ?? Mock.Of<IComplianceStatusService>(),
            new MigraDocPdfDocumentRenderer(),
            flags.Object,
            Options.Create(new ComplianceOptions()),
            NullLogger<ComplianceArchiveService>.Instance,
            new FakeTimeProvider(new DateTimeOffset(2026, 11, 6, 10, 0, 0, TimeSpan.Zero)));
        return (service, statements);
    }

    /// <summary>One row for each shape the services read, so the queries that come after the first are compiled too.</summary>
    private static DataTable? Rows(string sql)
    {
        var text = sql.TrimStart();
        if (Regex.IsMatch(text, "^SELECT EXISTS", RegexOptions.IgnoreCase))
            return Table([("value", typeof(bool))], true);

        // A count is answered by the probe (one row, the number it was built with), whatever tables its subqueries read: a collaborator's count names the flats
        // in a subquery, and must not be taken for the read of the flats.
        if (Regex.IsMatch(text, "^SELECT\\s+COUNT\\(", RegexOptions.IgnoreCase))
            return null;

        if (Regex.IsMatch(text, "FROM \"ComplianceSendLogEntries\"", RegexOptions.IgnoreCase))
        {
            return Table(
                [("Id", typeof(Guid)), ("Kind", typeof(int)), ("Sender", typeof(int)), ("SentAt", typeof(DateTime)), ("Reference", typeof(string)), ("ByUserId", typeof(string))],
                Guid.NewGuid(), (int)ComplianceSendKind.AlloggiatiGuests, (int)ComplianceSender.Manual, Arrival, DBNull.Value, "auth0|titolare");
        }

        if (Regex.IsMatch(text, "FROM \"Users\"", RegexOptions.IgnoreCase))
            return Table([("Id", typeof(string)), ("FirstName", typeof(string)), ("LastName", typeof(string))], "auth0|titolare", "Mario", "Rossi");

        if (Regex.IsMatch(text, "FROM \"ManualComplianceProofs\"", RegexOptions.IgnoreCase) && text.Contains("\"FileKey\"", StringComparison.Ordinal))
        {
            // The download asks for the kind, the day, the key and the type; the export for the whole row, key last.
            return text.Contains("\"Name\"", StringComparison.Ordinal)
                ? Table(
                    [("Id", typeof(Guid)), ("Kind", typeof(int)), ("PropertyId", typeof(Guid)), ("Name", typeof(string)), ("Day", typeof(DateOnly)), ("ContentType", typeof(string)), ("UploadedAt", typeof(DateTime)), ("FileKey", typeof(string))],
                    ProofId, (int)ManualProofKind.AlloggiatiReceipt, PropertyId, "Trullo", DateOnly.FromDateTime(Arrival), "application/pdf", Arrival, $"compliance-proofs/{OrgId}/{ProofId}/a.pdf")
                : Table(
                    [("Kind", typeof(int)), ("Day", typeof(DateOnly)), ("FileKey", typeof(string)), ("ContentType", typeof(string))],
                    (int)ManualProofKind.AlloggiatiReceipt, DateOnly.FromDateTime(Arrival), $"compliance-proofs/{OrgId}/{ProofId}/a.pdf", "application/pdf");
        }

        if (Regex.IsMatch(text, "FROM \"ManualComplianceProofs\"", RegexOptions.IgnoreCase) && text.Contains("\"Name\"", StringComparison.Ordinal))
        {
            return Table(
                [("Id", typeof(Guid)), ("Kind", typeof(int)), ("PropertyId", typeof(Guid)), ("Name", typeof(string)), ("Day", typeof(DateOnly)), ("ContentType", typeof(string)), ("UploadedAt", typeof(DateTime))],
                ProofId, (int)ManualProofKind.AlloggiatiReceipt, PropertyId, "Trullo", DateOnly.FromDateTime(Arrival), "application/pdf", Arrival);
        }

        // The flats each proof of a page covers (E20/F02/US01/T01): the proof, the flat and its name.
        if (Regex.IsMatch(text, "FROM \"ManualComplianceProofProperties\"", RegexOptions.IgnoreCase)
            && text.Contains("\"ProofId\"", StringComparison.Ordinal)
            && !Regex.IsMatch(text, "FROM \"ManualComplianceProofs\"", RegexOptions.IgnoreCase))
        {
            return Table([("ProofId", typeof(Guid)), ("PropertyId", typeof(Guid)), ("Name", typeof(string))], ProofId, OtherPropertyId, "Casa Bianca");
        }

        if (Regex.IsMatch(text, "FROM \"Bookings\"", RegexOptions.IgnoreCase))
        {
            // The stay of the booking page (id, property, arrival) and the stay of the report (id, property, name, code, arrival).
            return text.Contains("\"BookingCode\"", StringComparison.Ordinal)
                ? Table(
                    [("Id", typeof(Guid)), ("PropertyId", typeof(Guid)), ("Name", typeof(string)), ("BookingCode", typeof(string)), ("CheckInDate", typeof(DateTime))],
                    BookingId, PropertyId, "Trullo", "ABCDE-FGHIJ", Arrival)
                : Table([("Id", typeof(Guid)), ("PropertyId", typeof(Guid)), ("CheckInDate", typeof(DateTime))], BookingId, PropertyId, Arrival);
        }

        if (Regex.IsMatch(text, "FROM \"Properties\"", RegexOptions.IgnoreCase))
            return Table([("Id", typeof(Guid)), ("Name", typeof(string)), ("Country", typeof(string))], PropertyId, "Trullo", "IT");

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

    // ─── The list, the download, the stay, the export ───────────────────────────────────────────────────

    /// <summary>
    /// PROOF TEST: data and permission rule (shape of the statement). The list is a count and a page, both naming the org of the scope on the
    /// proofs next to the tenant filter of the context; the reach of a collaborator «Solo alcuni» is an EXISTS on the grants in the same statement and
    /// that of an account in no team the owner of the property, the whole org adds nothing; the deleted properties are not cut out (their history is
    /// kept) and the page is ordered and paged by the database.
    /// </summary>
    [Fact]
    public async Task ComplianceArchive_ListOrgAndReach_AreInTheSql_Test()
    {
        async Task<List<string>> Run(HostScope scope)
        {
            var (service, statements) = Build();
            await NpgsqlTranslationProbe.AssertTranslatesAsync(() => service.ListAsync(
                scope,
                new ComplianceArchiveQuery(null, new DateOnly(2026, 1, 1), new DateOnly(2026, 12, 31), ManualProofKind.IstatProof, 2, 10),
                CancellationToken.None));
            return statements;
        }

        var wholeOrg = await Run(OrgWide);
        var limited = await Run(Restricted);
        var owned = await Run(Owned);

        // The total and the page of the proofs, then the flats each proof of the page covers (E20/F02/US01/T01) in one statement for the whole page.
        Assert.Equal(3, wholeOrg.Count);
        Assert.Equal(3, limited.Count);
        Assert.Equal(3, owned.Count);
        Assert.StartsWith("SELECT count(*)", wholeOrg[0].TrimStart(), StringComparison.OrdinalIgnoreCase);
        Assert.Matches("ORDER BY .*\"UploadedAt\" DESC, .*\"Id\" DESC", wholeOrg[1]);
        Assert.Contains("LIMIT", wholeOrg[1], StringComparison.Ordinal);
        Assert.Contains("OFFSET", wholeOrg[1], StringComparison.Ordinal);
        foreach (var sql in wholeOrg.Take(2).Concat(limited.Take(2)).Concat(owned.Take(2)))
        {
            Assert.Contains("FROM \"ManualComplianceProofs\"", sql, StringComparison.Ordinal);
            Assert.Matches("\"OrgId\" = @", sql);
            Assert.Contains("@ef_filter", sql, StringComparison.Ordinal);
            Assert.Contains("\"Kind\" = ", sql, StringComparison.Ordinal);
            Assert.DoesNotContain("IsDeleted", sql, StringComparison.Ordinal);
        }

        // The flats of the page: of the org, of the proofs of the page, the history of deleted flats kept.
        foreach (var coverage in new[] { wholeOrg[2], limited[2], owned[2] })
        {
            Assert.Contains("FROM \"ManualComplianceProofProperties\"", coverage, StringComparison.Ordinal);
            Assert.Matches("\"OrgId\" = @", coverage);
            Assert.DoesNotContain("IsDeleted", coverage, StringComparison.Ordinal);
        }

        Assert.DoesNotContain(wholeOrg, sql => sql.Contains("PropertyMemberAccesses", StringComparison.Ordinal));

        // The reach of a collaborator is in the statement of the proofs, and a receipt of several flats is read only when every flat it covers is in reach: a
        // NOT EXISTS over the lines of the flats of the proof, with the same grants.
        Assert.All(limited.Take(2), sql =>
        {
            Assert.Contains("EXISTS", sql, StringComparison.Ordinal);
            Assert.Contains("PropertyMemberAccesses", sql, StringComparison.Ordinal);
            Assert.Contains("NOT EXISTS", sql, StringComparison.Ordinal);
            Assert.Contains("\"ManualComplianceProofProperties\"", sql, StringComparison.Ordinal);
        });
        Assert.All(owned.Take(2), sql =>
        {
            Assert.Contains("\"OwnerId\" = @", sql, StringComparison.Ordinal);
            Assert.Contains("\"ManualComplianceProofProperties\"", sql, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task ComplianceArchive_AFilterByPropertyAndTheDownload_TranslateWithTheReachInTheSql_Test()
    {
        var (service, statements) = Build();
        await NpgsqlTranslationProbe.AssertTranslatesAsync(() => service.ListAsync(
            Restricted, new ComplianceArchiveQuery(PropertyId, null, null, null), CancellationToken.None));
        await NpgsqlTranslationProbe.AssertTranslatesAsync(() => service.OpenDownloadAsync(Restricted, ProofId, moneyProofs: true, CancellationToken.None));

        // The property filter is checked first, in the reach; the download names the proof, the org and the reach in one statement.
        Assert.Contains(statements, sql => sql.TrimStart().StartsWith("SELECT EXISTS", StringComparison.OrdinalIgnoreCase)
                                          && sql.Contains("PropertyMemberAccesses", StringComparison.Ordinal));
        var download = Assert.Single(statements, sql => sql.Contains("\"FileKey\"", StringComparison.Ordinal) && !sql.Contains("\"Name\"", StringComparison.Ordinal));
        Assert.Matches("\"OrgId\" = @", download);
        Assert.Matches("\"Id\" = @", download);
        Assert.Contains("PropertyMemberAccesses", download, StringComparison.Ordinal);
    }

    /// <summary>
    /// PROOF TEST: money and permission rule (shape of the statement). The records of money (the receipts of a tourist tax declaration or payment) are
    /// cut out by the statement of the list, of its total, of the download and of the export when the caller may not read money, and are not cut when
    /// it may: the database never returns them, so no later step can forget to drop them.
    /// </summary>
    [Fact]
    public async Task ComplianceArchive_RecordsOfMoney_AreCutInTheSql_ForWhoMayNotReadMoney_Test()
    {
        async Task<List<string>> Run(bool moneyProofs)
        {
            var (service, statements) = Build();
            await NpgsqlTranslationProbe.AssertTranslatesAsync(() => service.ListAsync(
                OrgWide, new ComplianceArchiveQuery(MoneyProofs: moneyProofs), CancellationToken.None));
            await NpgsqlTranslationProbe.AssertTranslatesAsync(() => service.OpenDownloadAsync(OrgWide, ProofId, moneyProofs, CancellationToken.None));
            await NpgsqlTranslationProbe.AssertTranslatesAsync(() => service.ExportAsync(
                OrgWide, new ComplianceArchiveExportQuery(new DateOnly(2026, 10, 1), new DateOnly(2026, 11, 30), PropertyId, moneyProofs), CancellationToken.None));
            return statements.Where(sql => sql.Contains("FROM \"ManualComplianceProofs\"", StringComparison.Ordinal)).ToList();
        }

        var withoutMoney = await Run(false);
        var withMoney = await Run(true);

        // The total and the page of the list, the download, the export: four statements on the proofs, each of them cut (or none of them).
        Assert.Equal(4, withoutMoney.Count);
        Assert.Equal(4, withMoney.Count);
        Assert.All(withoutMoney, sql => Assert.Matches("\"Kind\" NOT IN \\(", sql));
        Assert.All(withMoney, sql => Assert.DoesNotMatch("\"Kind\" NOT IN \\(", sql));
        Assert.All(withoutMoney.Concat(withMoney), sql => Assert.Matches("\"OrgId\" = @", sql));
    }

    [Fact]
    public async Task ComplianceArchive_TheStayAndItsLog_TranslateWithTheReachInTheSql_Test()
    {
        var (service, statements) = Build();

        await NpgsqlTranslationProbe.AssertTranslatesAsync(() => service.GetBookingComplianceAsync(Restricted, BookingId, CancellationToken.None));

        // The stay, its proofs, the flats each proof covers, its log, and the names of the people of the org who declared a send: five statements, each of the org.
        Assert.Equal(5, statements.Count);
        Assert.Contains("FROM \"Bookings\"", statements[0], StringComparison.Ordinal);
        Assert.Contains("FROM \"ManualComplianceProofs\"", statements[1], StringComparison.Ordinal);
        Assert.Contains("FROM \"ManualComplianceProofProperties\"", statements[2], StringComparison.Ordinal);
        Assert.Contains("FROM \"ComplianceSendLogEntries\"", statements[3], StringComparison.Ordinal);
        Assert.Contains("FROM \"Users\"", statements[4], StringComparison.Ordinal);
        Assert.All(statements, sql => Assert.Matches("\"OrgId\" = @", sql));
        Assert.All(new[] { statements[0], statements[1], statements[3] }, sql => Assert.Contains("PropertyMemberAccesses", sql, StringComparison.Ordinal));

        // The proofs of the stay are those that cover its flat on its arrival day, the flat they were filed under or another one of the receipt.
        Assert.Contains("\"ManualComplianceProofProperties\"", statements[1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task ComplianceArchive_TheExport_TranslatesAndReadsOnlyTheProofsOfThePeriodInTheReach_Test()
    {
        var (service, statements) = Build();

        await NpgsqlTranslationProbe.AssertTranslatesAsync(() => service.ExportAsync(
            Restricted, new ComplianceArchiveExportQuery(new DateOnly(2026, 10, 1), new DateOnly(2026, 11, 30), PropertyId), CancellationToken.None));

        var proofs = Assert.Single(statements, sql => sql.Contains("FROM \"ManualComplianceProofs\"", StringComparison.Ordinal));
        Assert.Matches("\"OrgId\" = @", proofs);
        Assert.Contains("PropertyMemberAccesses", proofs, StringComparison.Ordinal);
        Assert.Matches("ORDER BY .*\"Day\", .*\"UploadedAt\", .*\"Id\"", proofs);
        Assert.Contains("LIMIT", proofs, StringComparison.Ordinal);
    }

    // ─── The report and the upload ──────────────────────────────────────────────────────────────────────

    /// <summary>
    /// PROOF TEST: data rule (shape of the statement). The rows the report reads besides the traffic light's (the stays that owe nothing, the names of
    /// the properties and the codes of the stays) are all of the org, the no-show ones only from the active statuses and with the mark, and in the reach
    /// of the caller; none of them reads a guest.
    /// </summary>
    [Fact]
    public async Task ComplianceReport_TheRowsItAddsToTheTrafficLight_AreOfTheOrgAndTheReach_AndReadNoGuest_Test()
    {
        var status = new Mock<IComplianceStatusService>();
        status
            .Setup(s => s.EvaluateAsync(It.IsAny<HostScope>(), It.IsAny<ComplianceStatusWindow>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ComplianceStatusResult(
                [
                    new ComplianceStatusItem(
                        ComplianceItemKind.Alloggiati,
                        ComplianceCockpitAction.SendAlloggiati,
                        PropertyId,
                        BookingId,
                        null,
                        "Anna Verdi",
                        ComplianceLightRules.Alloggiati(MissingFacts()).Verdict,
                        null),
                ],
                ComplianceEvaluatedUniverse.None));
        var (service, statements) = Build(status: status.Object);

        await NpgsqlTranslationProbe.AssertTranslatesAsync(() => service.GetCoverageReportAsync(
            Restricted, new ComplianceReportQuery(new DateOnly(2026, 8, 1), new DateOnly(2026, 11, 6), PropertyId), CancellationToken.None));

        // The property filter, the no-show stays, the names of the properties and the codes of the stays.
        Assert.Equal(4, statements.Count);
        var noShows = Assert.Single(statements, sql => sql.Contains("\"NoShowAt\" IS NOT NULL", StringComparison.Ordinal));
        Assert.Matches("\"OrgId\" = @", noShows);
        Assert.Contains("PropertyMemberAccesses", noShows, StringComparison.Ordinal);
        Assert.Matches("\"Status\" IN \\(", noShows);
        Assert.All(statements, sql =>
        {
            Assert.Matches("\"OrgId\" = @", sql);
            Assert.DoesNotContain("\"Guests\"", sql, StringComparison.Ordinal);
            Assert.DoesNotContain("IsDeleted", sql, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task ComplianceArchive_TheUpload_ChecksTheProperty_InTheReachOfTheCaller_Test()
    {
        var (service, statements) = Build();

        await NpgsqlTranslationProbe.AssertTranslatesAsync(async () =>
        {
            await using var content = new MemoryStream("%PDF-1.4 TEST"u8.ToArray());
            await service.UploadProofAsync(
                Restricted,
                new UploadComplianceProofRequest(PropertyId, ManualProofKind.AlloggiatiReceipt, new DateOnly(2026, 11, 3), "auth0|collaboratore"),
                content,
                CancellationToken.None);
        });

        // The first statement is the check of the flat of the request (E20/F02/US01/T01: one statement for the flats a receipt covers; the next ones count the stays).
        var property = statements[0];
        Assert.Contains("FROM \"Properties\"", property, StringComparison.Ordinal);
        Assert.Matches("\"OrgId\" = @", property);
        Assert.Contains("PropertyMemberAccesses", property, StringComparison.Ordinal);
        Assert.Matches("\"Id\" = @", property);
    }

    /// <summary>
    /// PROOF TEST: data and permission rule (shape of the statements, E20/F02/US01/T01). The flats a receipt covers are checked in ONE statement of the org and the
    /// reach of the caller (the ids as one array, not a query for each flat), and the stays the receipt proves are counted by the database, flat by flat, from the one
    /// definition of a stay that owes the communication: of the org, in the reach, in an active status with no no-show mark, arrived within the Europe/Rome day.
    /// </summary>
    [Fact]
    public async Task ComplianceArchive_TheCoverageOfSeveralFlats_IsOneStatementOfTheOrgAndTheReach_AndTheStaysAreCountedInSql_Test()
    {
        var other = OtherPropertyId;
        DataTable? TwoFlats(string sql)
        {
            // The count of the stays reads the flats in a subquery too: what is read first (FROM) decides what the statement is.
            if (Regex.IsMatch(sql, "FROM \"Bookings\"", RegexOptions.IgnoreCase))
                return Table([("PropertyId", typeof(Guid)), ("Stays", typeof(int))], PropertyId, 2);

            if (Regex.IsMatch(sql, "FROM \"Properties\"", RegexOptions.IgnoreCase))
            {
                var flats = Table([("Id", typeof(Guid)), ("Name", typeof(string)), ("Country", typeof(string))], PropertyId, "Trullo", "IT");
                flats.Rows.Add(other, "Casa Bianca", "IT");
                return flats;
            }

            return Rows(sql);
        }

        var (preview, previewStatements) = Build(TwoFlats);
        await NpgsqlTranslationProbe.AssertTranslatesAsync(() => preview.GetReceiptCoverageAsync(
            Restricted, new DateOnly(2026, 11, 3), [PropertyId, other], CancellationToken.None));
        var (upload, uploadStatements) = Build(TwoFlats);
        await NpgsqlTranslationProbe.AssertTranslatesAsync(async () =>
        {
            await using var content = new MemoryStream("%PDF-1.4 TEST"u8.ToArray());
            await upload.UploadProofAsync(
                Restricted,
                new UploadComplianceProofRequest(PropertyId, ManualProofKind.AlloggiatiReceipt, new DateOnly(2026, 11, 3), "auth0|collaboratore", [other]),
                content,
                CancellationToken.None);
        });

        foreach (var statements in new[] { previewStatements, uploadStatements })
        {
            var flats = statements[0];
            Assert.Contains("FROM \"Properties\"", flats, StringComparison.Ordinal);
            Assert.Matches("\"OrgId\" = @", flats);
            Assert.Contains("PropertyMemberAccesses", flats, StringComparison.Ordinal);
            Assert.Matches("\"Id\" = ANY", flats);

            var stays = statements[1];
            Assert.Contains("FROM \"Bookings\"", stays, StringComparison.Ordinal);
            Assert.Matches("\"OrgId\" = @", stays);
            Assert.Contains("PropertyMemberAccesses", stays, StringComparison.Ordinal);
            Assert.Matches("\"Status\" IN \\(", stays);
            Assert.Contains("\"NoShowAt\" IS NULL", stays, StringComparison.Ordinal);
            Assert.Matches("\"CheckInDate\" >= @", stays);
            Assert.Matches("\"CheckInDate\" < @", stays);
            Assert.Contains("GROUP BY", stays, StringComparison.Ordinal);
            Assert.DoesNotContain("\"Guests\"", stays, StringComparison.Ordinal);
        }

        // The summary stops after the count: two statements, none for a guest, a proof or a file.
        Assert.Equal(2, previewStatements.Count);
    }

    /// <summary>
    /// PROOF TEST: data rule (shape of the statement, E20/F02/US01/T01). The night's reading of the proofs past their five years is ONE statement Npgsql can type (a
    /// window of the oldest first, with the lines of the flats each proof covers, which go with it) that cuts on the date of the upload alone and names no tenant: a job
    /// has none, and a proof younger than five years is never read for deletion.
    /// </summary>
    [Fact]
    public async Task ComplianceProofRetention_TheReadingOfTheDueProofs_TranslatesAndCutsOnTheUploadDate_Test()
    {
        var statements = new List<string>();
        var db = NpgsqlTranslationProbe.NewContext(statements);
        var job = new ComplianceProofRetentionJob(
            db,
            new SigningStorage(),
            new FakeTimeProvider(new DateTimeOffset(2026, 11, 6, 10, 0, 0, TimeSpan.Zero)),
            NullLogger<ComplianceProofRetentionJob>.Instance);

        await NpgsqlTranslationProbe.AssertTranslatesAsync(() => job.ExecuteAsync(CancellationToken.None));

        var due = Assert.Single(statements);
        Assert.Contains("FROM \"ManualComplianceProofs\"", due, StringComparison.Ordinal);
        Assert.Contains("\"ManualComplianceProofProperties\"", due, StringComparison.Ordinal);
        Assert.Matches("\"UploadedAt\" < @", due);
        Assert.Matches("ORDER BY .*\"UploadedAt\"", due);
        Assert.Contains("LIMIT", due, StringComparison.Ordinal);
        Assert.DoesNotContain("@ef_filter", due, StringComparison.Ordinal);
    }

    private static AlloggiatiFacts MissingFacts() => new()
    {
        Sender = ComplianceSenderType.Manual,
        Status = AlloggiatiWebStatus.DaInviareManualmente,
        StayNights = 2,
        DeadlineUtc = new DateTime(2026, 11, 5, 0, 0, 0, DateTimeKind.Utc),
        NowUtc = new DateTime(2026, 11, 6, 10, 0, 0, DateTimeKind.Utc),
        TodayRome = new DateOnly(2026, 11, 6),
    };

    /// <summary>A private bucket that signs and keeps nothing: the probes never reach a file.</summary>
    private sealed class SigningStorage : IFileStorage
    {
        public Task PutAsync(StorageBucket bucket, string key, Stream content, string contentType, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<Stream?> OpenReadAsync(StorageBucket bucket, string key, CancellationToken cancellationToken = default) =>
            Task.FromResult<Stream?>(new MemoryStream("%PDF-1.4 TEST"u8.ToArray()));

        public Task<bool> ExistsAsync(StorageBucket bucket, string key, CancellationToken cancellationToken = default) => Task.FromResult(true);

        public Task DeleteAsync(StorageBucket bucket, string key, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public string GetPublicUrl(string key) => throw new InvalidOperationException("A compliance proof has no public address.");

        public string? TryGetPublicKey(string url) => null;

        public Task<Uri?> GetSignedReadUrlAsync(string key, TimeSpan lifetime, string? downloadFileName, CancellationToken cancellationToken = default) =>
            Task.FromResult<Uri?>(new Uri($"https://storage.example/private/{key}?signature=test"));
    }
}
