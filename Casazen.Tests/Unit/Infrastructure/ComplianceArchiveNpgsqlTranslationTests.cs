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
            return Table([("Id", typeof(Guid)), ("Name", typeof(string))], PropertyId, "Trullo");

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

        Assert.Equal(2, wholeOrg.Count);
        Assert.StartsWith("SELECT count(*)", wholeOrg[0].TrimStart(), StringComparison.OrdinalIgnoreCase);
        Assert.Matches("ORDER BY .*\"UploadedAt\" DESC, .*\"Id\" DESC", wholeOrg[1]);
        Assert.Contains("LIMIT", wholeOrg[1], StringComparison.Ordinal);
        Assert.Contains("OFFSET", wholeOrg[1], StringComparison.Ordinal);
        foreach (var sql in wholeOrg.Concat(limited).Concat(owned))
        {
            Assert.Contains("FROM \"ManualComplianceProofs\"", sql, StringComparison.Ordinal);
            Assert.Matches("\"OrgId\" = @", sql);
            Assert.Contains("@ef_filter", sql, StringComparison.Ordinal);
            Assert.Contains("\"Kind\" = ", sql, StringComparison.Ordinal);
            Assert.DoesNotContain("IsDeleted", sql, StringComparison.Ordinal);
        }

        Assert.DoesNotContain(wholeOrg, sql => sql.Contains("PropertyMemberAccesses", StringComparison.Ordinal));
        Assert.All(limited, sql =>
        {
            Assert.Contains("EXISTS", sql, StringComparison.Ordinal);
            Assert.Contains("PropertyMemberAccesses", sql, StringComparison.Ordinal);
        });
        Assert.All(owned, sql => Assert.Contains("\"OwnerId\" = @", sql, StringComparison.Ordinal));
    }

    [Fact]
    public async Task ComplianceArchive_AFilterByPropertyAndTheDownload_TranslateWithTheReachInTheSql_Test()
    {
        var (service, statements) = Build();
        await NpgsqlTranslationProbe.AssertTranslatesAsync(() => service.ListAsync(
            Restricted, new ComplianceArchiveQuery(PropertyId, null, null, null), CancellationToken.None));
        await NpgsqlTranslationProbe.AssertTranslatesAsync(() => service.OpenDownloadAsync(Restricted, ProofId, CancellationToken.None));

        // The property filter is checked first, in the reach; the download names the proof, the org and the reach in one statement.
        Assert.Contains(statements, sql => sql.TrimStart().StartsWith("SELECT EXISTS", StringComparison.OrdinalIgnoreCase)
                                          && sql.Contains("PropertyMemberAccesses", StringComparison.Ordinal));
        var download = Assert.Single(statements, sql => sql.Contains("\"FileKey\"", StringComparison.Ordinal) && !sql.Contains("\"Name\"", StringComparison.Ordinal));
        Assert.Matches("\"OrgId\" = @", download);
        Assert.Matches("\"Id\" = @", download);
        Assert.Contains("PropertyMemberAccesses", download, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ComplianceArchive_TheStayAndItsLog_TranslateWithTheReachInTheSql_Test()
    {
        var (service, statements) = Build();

        await NpgsqlTranslationProbe.AssertTranslatesAsync(() => service.GetBookingComplianceAsync(Restricted, BookingId, CancellationToken.None));

        // The stay, its proofs, its log, and the names of the people of the org who declared a send: four statements, each of the org.
        Assert.Equal(4, statements.Count);
        Assert.Contains("FROM \"Bookings\"", statements[0], StringComparison.Ordinal);
        Assert.Contains("FROM \"ManualComplianceProofs\"", statements[1], StringComparison.Ordinal);
        Assert.Contains("FROM \"ComplianceSendLogEntries\"", statements[2], StringComparison.Ordinal);
        Assert.Contains("FROM \"Users\"", statements[3], StringComparison.Ordinal);
        Assert.All(statements, sql => Assert.Matches("\"OrgId\" = @", sql));
        Assert.All(statements.Take(3), sql => Assert.Contains("PropertyMemberAccesses", sql, StringComparison.Ordinal));
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

        var property = Assert.Single(statements, sql => sql.Contains("FROM \"Properties\"", StringComparison.Ordinal));
        Assert.Matches("\"OrgId\" = @", property);
        Assert.Contains("PropertyMemberAccesses", property, StringComparison.Ordinal);
        Assert.Matches("\"Id\" = @", property);
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
