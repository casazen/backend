using System.IO.Compression;
using System.Reflection;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Features;
using Casazen.Core.Options;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Documents;
using Casazen.Infrastructure.Services;
using Casazen.Web.Authorization;
using Casazen.Web.Controllers;
using Casazen.Web.DTOs.Compliance;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;
using World = Casazen.Tests.Unit.Services.ComplianceStatusServiceTests.World;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// The archive of the proofs, the log of what was sent and the coverage report (E03/F02/US03/T01, NF-47, NF-52, CHK-08, CHK-12). One proof for
/// each rule that costs if it fails: what a caller may see and download (data and permission), what the report counts as evidence (data), what an
/// upload accepts (data) and who may upload a tax proof (permission). The services run on the real context and the real rules of the traffic
/// light; the storage is an in-memory double that refuses to give a public address.
/// </summary>
public sealed class ComplianceArchiveProofTests
{
    private static readonly byte[] PdfBytes = Encoding.ASCII.GetBytes("%PDF-1.4 TEST PROOF");

    /// <summary>The arrival day of the stays of the tests: 3 days before the clock of the world (2026-11-06).</summary>
    private static readonly DateOnly ArrivalDay = new(2026, 11, 3);

    private static DateOnly DayOf(int offsetFromToday) => new DateOnly(2026, 11, 6).AddDays(offsetFromToday);

    // ─── The reach of the caller ────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// PROOF TEST: data and permission rule. A proof of another org, or of a property outside the caller's reach (a collaborator «Solo alcuni»), is
    /// neither listed, nor downloadable, nor exported, nor asked for by property, nor shown with its stay, nor uploaded to: every one answers 404 like a
    /// proof that does not exist, and nothing is stored. The download is a signed address of the private bucket, never a public one.
    /// </summary>
    [Fact]
    public async Task ComplianceArchive_OtherOrgOrScope_NeverListedOrDownloadable_Test()
    {
        var world = await World.SeedAsync();
        var storage = new MemoryStorage { Signs = true };
        var granted = await AddProofAsync(world, world.Org.Id, world.Granted.Id, ManualProofKind.AlloggiatiReceipt, ArrivalDay, storage);
        var hidden = await AddProofAsync(world, world.Org.Id, world.Hidden.Id, ManualProofKind.AlloggiatiReceipt, ArrivalDay, storage);
        var foreign = await AddProofAsync(world, world.Foreign.OrgId, world.Foreign.Id, ManualProofKind.AlloggiatiReceipt, ArrivalDay, storage);
        var (grantedStay, _) = await world.AddStayAsync(world.Granted, checkInOffset: -3);
        var (hiddenStay, _) = await world.AddStayAsync(world.Hidden, checkInOffset: -3);
        var (foreignStay, _) = await world.AddStayAsync(world.Foreign, checkInOffset: -3);
        await using var db = world.Db();
        db.ComplianceSendLogEntries.AddRange(
            LogLine(world.Granted, grantedStay, ComplianceSender.ChannelManager),
            LogLine(world.Hidden, hiddenStay, ComplianceSender.Manual),
            LogLine(world.Foreign, foreignStay, ComplianceSender.Manual));
        await db.SaveChangesAsync();
        var service = NewService(world, db, storage);

        // The list: the collaborator sees the proof of its property; the whole org sees both of its own, and never the other org's.
        var restricted = await service.ListAsync(world.Restricted, new ComplianceArchiveQuery(), CancellationToken.None);
        Assert.Equal([granted.Id], restricted.Items.Select(i => i.Id));
        Assert.Equal(1, restricted.TotalCount);
        var orgWide = await service.ListAsync(world.OrgWide, new ComplianceArchiveQuery(), CancellationToken.None);
        Assert.Equal(new[] { granted.Id, hidden.Id }.Order(), orgWide.Items.Select(i => i.Id).Order());
        Assert.DoesNotContain(orgWide.Items, i => i.Id == foreign.Id || i.PropertyId == world.Foreign.Id);

        // Asking for a property outside the reach is a 404, not an empty list that says it exists.
        await Assert.ThrowsAsync<NotFoundException>(
            () => service.ListAsync(world.Restricted, new ComplianceArchiveQuery(PropertyId: world.Hidden.Id), CancellationToken.None));
        await Assert.ThrowsAsync<NotFoundException>(
            () => service.ListAsync(world.OrgWide, new ComplianceArchiveQuery(PropertyId: world.Foreign.Id), CancellationToken.None));

        // The download: the proof of the property it reaches answers a signed address; the others, the same 404 as an id that never existed.
        var download = await service.OpenDownloadAsync(world.Restricted, granted.Id, moneyProofs: true, CancellationToken.None);
        Assert.Null(download.Content);
        Assert.StartsWith("https://storage.example/private/compliance-proofs/", download.SignedUrl!.AbsoluteUri, StringComparison.Ordinal);
        Assert.Equal(TimeSpan.FromSeconds(120), storage.LastLifetime);
        Assert.Equal($"alloggiati-{ArrivalDay:yyyy-MM-dd}.pdf", download.FileName);
        foreach (var (scope, id) in new[] { (world.Restricted, hidden.Id), (world.OrgWide, foreign.Id), (world.OrgWide, Guid.NewGuid()) })
        {
            var refused = await Assert.ThrowsAsync<NotFoundException>(() => service.OpenDownloadAsync(scope, id, moneyProofs: true, CancellationToken.None));
            Assert.Equal(ComplianceArchiveErrorCodes.ProofNotFound, refused.Code);
        }

        // The stay: its proofs and its log for the reach that has it; the hidden stay and the other org's are a 404.
        var stay = await service.GetBookingComplianceAsync(world.Restricted, grantedStay.Id, CancellationToken.None);
        Assert.Equal([granted.Id], stay.Proofs.Select(p => p.Id));
        var line = Assert.Single(stay.SendLog);
        Assert.Equal(ComplianceSender.ChannelManager, line.Sender);
        foreach (var (scope, id) in new[] { (world.Restricted, hiddenStay.Id), (world.OrgWide, foreignStay.Id) })
        {
            var refused = await Assert.ThrowsAsync<NotFoundException>(() => service.GetBookingComplianceAsync(scope, id, CancellationToken.None));
            Assert.Equal(ComplianceArchiveErrorCodes.BookingNotFound, refused.Code);
        }

        // The export of the period holds only what the caller reaches, and opens no other file.
        storage.Opened.Clear();
        await using (var export = await service.ExportAsync(world.Restricted, new ComplianceArchiveExportQuery(ArrivalDay, ArrivalDay), CancellationToken.None))
        {
            Assert.Equal(1, export.FileCount);
            Assert.Equal([granted.FileKey], storage.Opened);
            using var zip = new ZipArchive(export.Content, ZipArchiveMode.Read);
            var index = ReadEntry(zip, "index.csv");
            Assert.Contains("Trullo", index, StringComparison.Ordinal);
            Assert.DoesNotContain("Casa Bianca", index, StringComparison.Ordinal);
            Assert.DoesNotContain("Villa Altrove", index, StringComparison.Ordinal);

            // And so does the coverage of the pack: the stay of the property the caller reaches, never the others.
            var coverage = ReadEntry(zip, ComplianceArchiveDocuments.CoverageFileName);
            Assert.Contains("Trullo", coverage, StringComparison.Ordinal);
            Assert.DoesNotContain("Casa Bianca", coverage, StringComparison.Ordinal);
            Assert.DoesNotContain("Villa Altrove", coverage, StringComparison.Ordinal);
        }

        // An upload to a property outside the reach stores nothing.
        await Assert.ThrowsAsync<NotFoundException>(async () =>
        {
            await using var content = new MemoryStream(PdfBytes);
            await service.UploadProofAsync(
                world.Restricted,
                new UploadComplianceProofRequest(world.Hidden.Id, ManualProofKind.AlloggiatiReceipt, ArrivalDay, world.CollaboratorId),
                content,
                CancellationToken.None);
        });
        Assert.Empty(storage.Puts);
        Assert.Equal(3, await db.ManualComplianceProofs.CountAsync());
    }

    // ─── Records of money ───────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// PROOF TEST: money and permission rule (AM-03). The receipt of a tourist tax declaration or payment is a record of money: for a caller who may
    /// not read money (the collaborator holds no <c>payment.read</c>) it is not in the list, not in its total, not in the ZIP, its index or its count,
    /// and its download is the 404 of a proof that does not exist, signing nothing and opening no file; for a caller who may (the accountant) all
    /// four kinds are there. The stay never carries a tax receipt, whoever asks. The cut is by the rule of the kinds, not by a list of the test.
    /// </summary>
    [Fact]
    public async Task ComplianceArchive_TaxReceiptsAreRecordsOfMoney_OnlyForWhoMayReadMoney_Test()
    {
        var world = await World.SeedAsync();
        var storage = new MemoryStorage { Signs = true };
        var alloggiati = await AddProofAsync(world, world.Org.Id, world.Granted.Id, ManualProofKind.AlloggiatiReceipt, ArrivalDay, storage);
        var istat = await AddProofAsync(world, world.Org.Id, world.Granted.Id, ManualProofKind.IstatProof, new DateOnly(2026, 10, 1), storage);
        var declaration = await AddProofAsync(world, world.Org.Id, world.Granted.Id, ManualProofKind.TouristTaxDeclaration, new DateOnly(2026, 10, 1), storage);
        var payment = await AddProofAsync(world, world.Org.Id, world.Granted.Id, ManualProofKind.TouristTaxPayment, new DateOnly(2026, 10, 1), storage);
        var hiddenPayment = await AddProofAsync(world, world.Org.Id, world.Hidden.Id, ManualProofKind.TouristTaxPayment, new DateOnly(2026, 10, 1), storage);

        // A tax receipt that carries the arrival day of a stay (it is filed under the first day of a month, so a stay arriving on it meets it).
        var (stay, _) = await world.AddStayAsync(world.Granted, checkInOffset: -3);
        var onArrivalDay = await AddProofAsync(world, world.Org.Id, world.Granted.Id, ManualProofKind.TouristTaxPayment, ArrivalDay, storage);
        await using var db = world.Db();
        var service = NewService(world, db, storage);
        var period = new DateOnly(2026, 10, 1);

        // The list and its total: without money only the kinds that are not money, whatever kind is asked, for the collaborator's reach as for the org's.
        foreach (var scope in new[] { world.OrgWide, world.Restricted })
        {
            var withoutMoney = await service.ListAsync(scope, new ComplianceArchiveQuery(), CancellationToken.None);
            Assert.All(withoutMoney.Items, item => Assert.False(ComplianceArchiveRules.IsMoneyKind(item.Kind)));
            Assert.Equal(withoutMoney.Items.Count, withoutMoney.TotalCount);
            Assert.Contains(withoutMoney.Items, item => item.Id == alloggiati.Id);
            Assert.Contains(withoutMoney.Items, item => item.Id == istat.Id);

            foreach (var money in ComplianceArchiveRules.MoneyKinds)
            {
                var asked = await service.ListAsync(scope, new ComplianceArchiveQuery(Kind: money), CancellationToken.None);
                Assert.Empty(asked.Items);
                Assert.Equal(0, asked.TotalCount);
            }
        }

        var orgWithoutMoney = await service.ListAsync(world.OrgWide, new ComplianceArchiveQuery(), CancellationToken.None);
        Assert.Equal(2, orgWithoutMoney.TotalCount);
        var orgWithMoney = await service.ListAsync(world.OrgWide, new ComplianceArchiveQuery(MoneyProofs: true), CancellationToken.None);
        Assert.Equal(
            new[] { alloggiati.Id, istat.Id, declaration.Id, payment.Id, hiddenPayment.Id, onArrivalDay.Id }.Order(),
            orgWithMoney.Items.Select(i => i.Id).Order());
        Assert.Equal(6, orgWithMoney.TotalCount);
        Assert.Equal(
            ComplianceArchiveRules.MoneyKinds.Order(),
            orgWithMoney.Items.Select(i => i.Kind).Where(ComplianceArchiveRules.IsMoneyKind).Distinct().Order());

        // The same cut by the kinds the enum has: what is not a record of money is listed, what is, is not (a kind added later is decided by the rule).
        var seenWithout = orgWithoutMoney.Items.Select(i => i.Kind).Distinct().Order().ToList();
        Assert.Equal(Enum.GetValues<ManualProofKind>().Where(k => !ComplianceArchiveRules.IsMoneyKind(k)).Order(), seenWithout);

        // The download: a record of money is the 404 of a proof that does not exist (same code), nothing is signed and no file is opened; with money it is signed.
        var unknown = await Assert.ThrowsAsync<NotFoundException>(
            () => service.OpenDownloadAsync(world.OrgWide, Guid.NewGuid(), moneyProofs: false, CancellationToken.None));
        foreach (var record in new[] { declaration, payment, onArrivalDay })
        {
            var refused = await Assert.ThrowsAsync<NotFoundException>(
                () => service.OpenDownloadAsync(world.Restricted, record.Id, moneyProofs: false, CancellationToken.None));
            Assert.Equal((unknown.Code, unknown.MessageKey), (refused.Code, refused.MessageKey));
            Assert.Equal(ComplianceArchiveErrorCodes.ProofNotFound, refused.Code);
        }

        Assert.Empty(storage.Signed);
        Assert.Empty(storage.Opened);
        foreach (var record in new[] { declaration, payment })
        {
            var granted = await service.OpenDownloadAsync(world.Restricted, record.Id, moneyProofs: true, CancellationToken.None);
            Assert.StartsWith("https://storage.example/private/compliance-proofs/", granted.SignedUrl!.AbsoluteUri, StringComparison.Ordinal);
        }

        Assert.Equal([declaration.FileKey, payment.FileKey], storage.Signed);

        // A receipt that is not money downloads without it.
        Assert.NotNull((await service.OpenDownloadAsync(world.Restricted, alloggiati.Id, moneyProofs: false, CancellationToken.None)).SignedUrl);

        // The ZIP, its index and its count: the files of the money kinds are neither opened nor counted nor named.
        storage.Opened.Clear();
        await using (var plain = await service.ExportAsync(world.OrgWide, new ComplianceArchiveExportQuery(period, new DateOnly(2026, 11, 30)), CancellationToken.None))
        {
            Assert.Equal(2, plain.FileCount);
            Assert.Equal(new[] { alloggiati.FileKey, istat.FileKey }.Order(), storage.Opened.Order());
            using var zip = new ZipArchive(plain.Content, ZipArchiveMode.Read);
            var index = ReadEntry(zip, "index.csv").TrimStart((char)0xFEFF).Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
            Assert.Equal(3, index.Length);
            Assert.DoesNotContain(index, line => line.Contains("imposta di soggiorno", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(zip.Entries, e => e.FullName.Contains("imposta-soggiorno", StringComparison.Ordinal));
            Assert.DoesNotContain(zip.Entries, e => e.FullName.Contains(declaration.Id.ToString("N")[..8], StringComparison.Ordinal));
            Assert.DoesNotContain(zip.Entries, e => e.FullName.Contains(payment.Id.ToString("N")[..8], StringComparison.Ordinal));
        }

        storage.Opened.Clear();
        await using (var withMoney = await service.ExportAsync(
                         world.OrgWide, new ComplianceArchiveExportQuery(period, new DateOnly(2026, 11, 30), MoneyProofs: true), CancellationToken.None))
        {
            Assert.Equal(6, withMoney.FileCount);
            using var zip = new ZipArchive(withMoney.Content, ZipArchiveMode.Read);
            var index = ReadEntry(zip, "index.csv").TrimStart((char)0xFEFF).Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
            Assert.Equal(7, index.Length);
            Assert.Equal(1, index.Count(line => line.StartsWith("Ricevuta della dichiarazione dell'imposta di soggiorno", StringComparison.Ordinal)));
            Assert.Equal(3, index.Count(line => line.StartsWith("Ricevuta del pagamento dell'imposta di soggiorno", StringComparison.Ordinal)));
        }

        // The limit counts what the caller may take: the two proofs that are not money fit a limit of two, and with money the same period has six
        // and the whole export is refused.
        world.Options.Archive.ExportMaxFiles = 2;
        await using (var fits = await NewService(world, db, storage).ExportAsync(
                         world.OrgWide, new ComplianceArchiveExportQuery(period, new DateOnly(2026, 11, 30)), CancellationToken.None))
        {
            Assert.Equal(2, fits.FileCount);
        }

        var tooMany = await Assert.ThrowsAsync<DomainRuleException>(
            () => NewService(world, db, storage).ExportAsync(
                world.OrgWide, new ComplianceArchiveExportQuery(period, new DateOnly(2026, 11, 30), MoneyProofs: true), CancellationToken.None));
        Assert.Equal(ComplianceArchiveErrorCodes.ExportTooLarge, tooMany.Code);

        // The stay carries its Alloggiati receipt and never a tax receipt, even one filed under its arrival day.
        var ofStay = await service.GetBookingComplianceAsync(world.Restricted, stay.Id, CancellationToken.None);
        Assert.Equal([alloggiati.Id], ofStay.Proofs.Select(p => p.Id));
        Assert.DoesNotContain(ofStay.Proofs, p => p.Id == onArrivalDay.Id);
    }

    // ─── The report ─────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// PROOF TEST: data rule. A stay whose guests were entrusted to Octorate and for which CasaZen holds no proof is «external verification required»
    /// (source Octorate, handoff topic, nothing inferred) and is not evidence: the report is incomplete however many handoffs the host made. A stay
    /// with an archived proof is official proof, one with nothing is missing, a no-show owes nothing. The report names no guest, reaches back beyond
    /// the lookback of the cockpit, and the PDF and the CSV say «Da verificare nel Cassetto Compliance» for the Octorate-only row and never claim to
    /// be issued by an authority.
    /// </summary>
    [Fact]
    public async Task ComplianceReport_OctorateOnlyRowIsExternalVerificationRequired_Test()
    {
        var world = await World.SeedAsync();
        world.Senders[world.Granted.Id] = ComplianceSenderType.ChannelManager;
        var storage = new MemoryStorage();

        // Granted: a stay entrusted to the channel manager today (Octorate holds the outcome), one with nothing, a no-show.
        var (entrusted, _) = await world.AddStayAsync(world.Granted, checkInOffset: 0, nights: 3, guests: 2);
        await world.EntrustAsync(entrusted, positions: [0, 1]);
        var (nothing, _) = await world.AddStayAsync(world.Granted, checkInOffset: -4, nights: 2);
        var (noShow, _) = await world.AddStayAsync(world.Granted, checkInOffset: -5, nights: 2, noShow: true);

        // Hidden: a stay with the receipt uploaded for its arrival day, and one from 120 days ago (past the 90 days of the cockpit) with its receipt too.
        var (proven, _) = await world.AddStayAsync(world.Hidden, checkInOffset: -2);
        await AddProofAsync(world, world.Org.Id, world.Hidden.Id, ManualProofKind.AlloggiatiReceipt, DayOf(-2), storage);
        var (old, _) = await world.AddStayAsync(world.Hidden, checkInOffset: -120);
        await AddProofAsync(world, world.Org.Id, world.Hidden.Id, ManualProofKind.AlloggiatiReceipt, DayOf(-120), storage);
        var (hiddenNoShow, _) = await world.AddStayAsync(world.Hidden, checkInOffset: -6, noShow: true);

        // The host starts the handoff and comes back: a line of the activity log, and not a proof.
        await using var db = world.Db();
        db.OrgActivityEntries.Add(new OrgActivityEntry
        {
            OrgId = world.Org.Id,
            When = world.Clock.GetUtcNow().UtcDateTime,
            ActorUserId = world.Owner.Id,
            Area = OrgActivityArea.ShortRent,
            Type = OrgActivityType.OctorateComplianceHandoffStarted,
            SubjectType = OrgActivitySubjectType.Property,
            SubjectId = world.Granted.Id.ToString(),
            DetailsJson = "{}",
        });
        await db.SaveChangesAsync();

        var service = NewService(world, db, storage);
        var query = new ComplianceReportQuery(DayOf(-130), DayOf(0));
        var report = await service.GetCoverageReportAsync(world.OrgWide, query, CancellationToken.None);

        var external = Assert.Single(report.Rows, r => r.BookingId == entrusted.Id);
        Assert.Equal(ComplianceEvidenceClass.ExternalVerificationRequired, external.EvidenceClass);
        Assert.Equal(ComplianceEvidenceSources.Octorate, external.EvidenceSource);
        Assert.Equal(OctorateHandoffTopic.Alloggiati, external.HandoffTopic);
        Assert.Equal(ComplianceCockpitAction.UploadAlloggiatiReceipt, external.Action);
        Assert.Null(external.ProofAt);
        Assert.Equal(ComplianceItemKind.Alloggiati, external.Kind);

        Assert.Equal(ComplianceEvidenceClass.Missing, Assert.Single(report.Rows, r => r.BookingId == nothing.Id).EvidenceClass);
        var provenRow = Assert.Single(report.Rows, r => r.BookingId == proven.Id);
        Assert.Equal((ComplianceEvidenceClass.OfficialProof, ComplianceEvidenceSources.ManualUpload, (ComplianceCockpitAction?)null), (provenRow.EvidenceClass, provenRow.EvidenceSource, provenRow.Action));
        Assert.NotNull(provenRow.ProofAt);
        Assert.Equal(ComplianceEvidenceClass.OfficialProof, Assert.Single(report.Rows, r => r.BookingId == old.Id).EvidenceClass);
        foreach (var owesNothing in new[] { noShow, hiddenNoShow })
        {
            var row = Assert.Single(report.Rows, r => r.BookingId == owesNothing.Id);
            Assert.Equal((ComplianceEvidenceClass.NotApplicable, ComplianceReportReasons.NoShow), (row.EvidenceClass, row.Reason));
        }

        // Incomplete, and counted: one proof of Octorate does not exist, and the handoff changed nothing.
        Assert.False(report.Complete);
        Assert.Equal(new ComplianceCoverageCounts(2, 1, 1, 2), report.Counts);
        Assert.Equal(ComplianceItemKind.Alloggiati, Assert.Single(report.Rows.Select(r => r.Kind).Distinct()));

        // The cockpit stops at its lookback; the report reaches beyond it. No guest is named: the rows are codes and days.
        var cockpit = await world.StatusService(db).EvaluateAsync(world.OrgWide, CancellationToken.None);
        Assert.DoesNotContain(cockpit.Items, i => i.BookingId == old.Id);
        var json = JsonSerializer.Serialize(report);
        Assert.DoesNotContain("Verdi", json, StringComparison.Ordinal);
        Assert.DoesNotContain("Anna", json, StringComparison.Ordinal);

        // Without the Octorate-only stay the report of the other property is complete: the no-show owes nothing, the rest is proven.
        var hiddenReport = await service.GetCoverageReportAsync(world.OrgWide, query with { PropertyId = world.Hidden.Id }, CancellationToken.None);
        Assert.True(hiddenReport.Complete);
        Assert.Equal(new ComplianceCoverageCounts(2, 0, 0, 1), hiddenReport.Counts);

        // The CSV and the PDF: the row held only by Octorate says to check it there, with source Octorate, and never says it was sent or accepted.
        var csv = await service.ExportReportAsync(world.OrgWide, query, ComplianceReportFormat.Csv, CancellationToken.None);
        var csvText = Encoding.UTF8.GetString(csv.Content);
        var externalLine = csvText.Split("\r\n").Single(line => line.Contains("Da verificare nel Cassetto Compliance", StringComparison.Ordinal));
        Assert.Contains(";Octorate;", externalLine, StringComparison.Ordinal);
        Assert.DoesNotContain("Prova archiviata", externalLine, StringComparison.Ordinal);
        Assert.DoesNotContain("Verdi", csvText, StringComparison.Ordinal);
        Assert.Equal("text/csv", csv.ContentType);

        // The ZIP of the proofs carries the same coverage with the traffic light on: the row held only by Octorate says to check it there, and is no file.
        await using (var pack = await service.ExportAsync(world.OrgWide, new ComplianceArchiveExportQuery(query.From, query.To), CancellationToken.None))
        {
            using var zip = new ZipArchive(pack.Content, ZipArchiveMode.Read);
            var coverage = ReadEntry(zip, ComplianceArchiveDocuments.CoverageFileName);
            var packLine = coverage.Split("\r\n").Single(line => line.Contains("Da verificare nel Cassetto Compliance", StringComparison.Ordinal));
            Assert.Contains(";Octorate;", packLine, StringComparison.Ordinal);
            Assert.DoesNotContain("Verdi", coverage, StringComparison.Ordinal);
            Assert.Equal(2, pack.FileCount);
        }

        var pdf = await service.ExportReportAsync(world.OrgWide, query, ComplianceReportFormat.Pdf, CancellationToken.None);
        Assert.Equal("%PDF-", Encoding.ASCII.GetString(pdf.Content, 0, 5));
        var model = ComplianceArchiveDocuments.Pdf(report, "tutti");
        var table = Assert.Single(model.Blocks.OfType<Casazen.Core.Documents.PdfTable>());
        Assert.Single(table.Rows, row => row.Contains("Da verificare nel Cassetto Compliance") && row.Contains("Octorate"));
        Assert.Contains(
            model.Blocks.OfType<Casazen.Core.Documents.PdfParagraph>(),
            p => p.Text == ComplianceArchiveDocuments.NotIssuedByAnAuthority && p.Text.Contains("Non è emesso, certificato né verificato da alcuna autorità", StringComparison.Ordinal));
    }

    /// <summary>
    /// A report window reaches the stays that arrived before the lookback of the cockpit and the months that lie wholly inside it; it reads nothing
    /// from Octorate (a connection that Octorate says is inactive turns the stay red in the cockpit, and leaves it «external verification» in the
    /// report), holds none of the checks of the present of a property, and counts a property deleted since.
    /// </summary>
    [Fact]
    public async Task ComplianceStatus_AWindowReachesBeyondTheLookbackAndReadsNothingFromOctorate_Test()
    {
        var world = await World.SeedAsync();
        world.Senders[world.Granted.Id] = ComplianceSenderType.ChannelManager;
        world.Health[world.Granted.Id] = ConnectionHealthState.Inactive;
        var (entrusted, _) = await world.AddStayAsync(world.Granted, checkInOffset: 0, nights: 3, guests: 1);
        await world.EntrustAsync(entrusted, positions: [0]);
        var (old, _) = await world.AddStayAsync(world.Hidden, checkInOffset: -100, nights: 2, touristTax: 4m);

        await using var db = world.Db();
        var cockpit = await world.StatusService(db).EvaluateAsync(world.OrgWide, CancellationToken.None);
        Assert.Equal(ComplianceReasonCodes.ConnectionInactive, Assert.Single(cockpit.Items, i => i.BookingId == entrusted.Id).Verdict.Reason);
        Assert.DoesNotContain(cockpit.Items, i => i.BookingId == old.Id);

        var window = new ComplianceStatusWindow(DayOf(-130), DayOf(0));
        var report = await world.StatusService(db).EvaluateAsync(world.OrgWide, window, CancellationToken.None);
        var entrustedItem = Assert.Single(report.Items, i => i.BookingId == entrusted.Id);
        Assert.Equal(
            (ComplianceReasonCodes.EntrustedToChannelManager, ComplianceEvidenceClass.ExternalVerificationRequired),
            (entrustedItem.Verdict.Reason, entrustedItem.Verdict.EvidenceClass));
        Assert.Contains(report.Items, i => i.BookingId == old.Id);
        Assert.All(report.Items, i => Assert.Contains(i.Kind, new[] { ComplianceItemKind.Alloggiati, ComplianceItemKind.Istat, ComplianceItemKind.TouristTax }));

        // The month of the old stay (2026-07-29) lies wholly inside the window and is over: its tourist tax is looked at.
        Assert.Contains(report.Items, i => i.Kind == ComplianceItemKind.TouristTax && i.Period == "2026-07");

        // Only the property asked for.
        var onlyGranted = await world.StatusService(db).EvaluateAsync(world.OrgWide, window with { PropertyId = world.Granted.Id }, CancellationToken.None);
        Assert.All(onlyGranted.Items, i => Assert.Equal(world.Granted.Id, i.PropertyId));

        // A property deleted since keeps its history in the report (the tenant filter stays).
        await using (var edit = world.Db())
        {
            var hidden = await edit.Properties.SingleAsync(p => p.Id == world.Hidden.Id);
            hidden.IsDeleted = true;
            await edit.SaveChangesAsync();
        }

        await using var after = world.Db();
        Assert.DoesNotContain((await world.StatusService(after).EvaluateAsync(world.OrgWide, CancellationToken.None)).Items, i => i.PropertyId == world.Hidden.Id);
        var afterReport = await world.StatusService(after).EvaluateAsync(world.OrgWide, window, CancellationToken.None);
        Assert.Contains(afterReport.Items, i => i.BookingId == old.Id);
        Assert.DoesNotContain(afterReport.Items, i => i.PropertyId == world.Foreign.Id);

        // The closed months wholly inside the window: a month cut by it is not looked at, and the month in progress is not over.
        Assert.Equal(
            [new DateOnly(2026, 8, 1), new DateOnly(2026, 9, 1), new DateOnly(2026, 10, 1)],
            ComplianceStatusService.ClosedMonthsWithin(new DateOnly(2026, 11, 6), new DateOnly(2026, 7, 29), new DateOnly(2026, 11, 6)));
        Assert.Equal(
            [new DateOnly(2026, 7, 1), new DateOnly(2026, 8, 1), new DateOnly(2026, 9, 1), new DateOnly(2026, 10, 1)],
            ComplianceStatusService.ClosedMonthsWithin(new DateOnly(2026, 11, 6), DayOf(-130), DayOf(0)));
        Assert.Equal(
            [new DateOnly(2026, 9, 1)],
            ComplianceStatusService.ClosedMonthsWithin(new DateOnly(2026, 11, 6), new DateOnly(2026, 9, 1), new DateOnly(2026, 10, 30)));
    }

    // ─── Uploads and exports ────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// PROOF TEST: data rule. An upload is judged on its content, not on its name or the type the client declared: a PDF is stored in the private
    /// bucket under a key the server made and listed next to the others; something that only wears the name of a PDF is refused (415) with nothing
    /// stored; a file over the limit is refused as a whole (413); an empty one (422); a day in the future or a kind that does not exist (422). A proof
    /// about a month is filed under the first day of its month, the day the traffic light looks it up by.
    /// </summary>
    [Fact]
    public async Task ComplianceArchive_UploadIsJudgedOnItsContent_AndListedWithTheDownloadedReceipts_Test()
    {
        var world = await World.SeedAsync();
        world.Options.Archive.UploadMaxMegabytes = 1;
        var storage = new MemoryStorage();
        await using var db = world.Db();
        var service = NewService(world, db, storage);
        UploadComplianceProofRequest Request(ManualProofKind kind, DateOnly day) => new(world.Granted.Id, kind, day, world.Owner.Id);

        // A PDF about a month, uploaded with any day of it.
        await using (var pdf = new MemoryStream(PdfBytes))
        {
            var entry = await service.UploadProofAsync(world.OrgWide, Request(ManualProofKind.TouristTaxPayment, new DateOnly(2026, 10, 17)), pdf, CancellationToken.None);

            Assert.Equal((ManualProofKind.TouristTaxPayment, new DateOnly(2026, 10, 1), "2026-10"), (entry.Kind, entry.Day, entry.Period));
            Assert.Equal((ComplianceEvidenceSources.ManualUpload, "application/pdf"), (entry.Source, entry.ContentType));
            Assert.Equal(entry.ReceivedAt.AddYears(5), entry.RetainUntil);
        }

        var stored = Assert.Single(await db.ManualComplianceProofs.ToListAsync());
        Assert.Equal(new DateOnly(2026, 10, 1), stored.Day);
        Assert.Equal(world.Org.Id, stored.OrgId);
        Assert.Equal(world.Owner.Id, stored.UploadedByUserId);
        Assert.StartsWith($"compliance-proofs/{world.Org.Id}/{stored.Id}/", stored.FileKey, StringComparison.Ordinal);
        Assert.EndsWith(".pdf", stored.FileKey, StringComparison.Ordinal);
        Assert.Equal([(StorageBucket.Private, stored.FileKey)], storage.Puts.Select(key => (storage.LastBucket, key)));
        Assert.Equal(stored.Id, Assert.Single((await service.ListAsync(world.OrgWide, new ComplianceArchiveQuery(Kind: ManualProofKind.TouristTaxPayment, MoneyProofs: true), CancellationToken.None)).Items).Id);

        // A day-kind keeps its day; the traffic light finds it by property, kind and day.
        await using (var receipt = new MemoryStream(PdfBytes))
        {
            var entry = await service.UploadProofAsync(world.OrgWide, Request(ManualProofKind.AlloggiatiReceipt, DayOf(-2)), receipt, CancellationToken.None);
            Assert.Equal((DayOf(-2), (string?)null), (entry.Day, entry.Period));
        }

        // Refused: nothing more is stored.
        var puts = storage.Puts.Count;
        var exe = Encoding.ASCII.GetBytes("MZ\u0090 this is an executable named ricevuta.pdf");
        await using (var fake = new MemoryStream(exe))
        {
            var refused = await Assert.ThrowsAsync<DomainUnsupportedMediaTypeException>(
                () => service.UploadProofAsync(world.OrgWide, Request(ManualProofKind.AlloggiatiReceipt, DayOf(-1)), fake, CancellationToken.None));
            Assert.Equal(ComplianceArchiveErrorCodes.ProofTypeUnsupported, refused.Code);
        }

        var big = new byte[(1024 * 1024) + 1];
        PdfBytes.CopyTo(big, 0);
        await using (var tooLarge = new MemoryStream(big))
        {
            var refused = await Assert.ThrowsAsync<DomainPayloadTooLargeException>(
                () => service.UploadProofAsync(world.OrgWide, Request(ManualProofKind.AlloggiatiReceipt, DayOf(-1)), tooLarge, CancellationToken.None));
            Assert.Equal(ComplianceArchiveErrorCodes.ProofTooLarge, refused.Code);
        }

        await using (var atTheLimit = new MemoryStream(big, 0, 1024 * 1024))
            await service.UploadProofAsync(world.OrgWide, Request(ManualProofKind.AlloggiatiReceipt, DayOf(-1)), atTheLimit, CancellationToken.None);
        puts++;

        await using (var empty = new MemoryStream())
        {
            var refused = await Assert.ThrowsAsync<DomainRuleException>(
                () => service.UploadProofAsync(world.OrgWide, Request(ManualProofKind.AlloggiatiReceipt, DayOf(-1)), empty, CancellationToken.None));
            Assert.Equal(ComplianceArchiveErrorCodes.ProofFileInvalid, refused.Code);
        }

        await using (var content = new MemoryStream(PdfBytes))
        {
            var future = await Assert.ThrowsAsync<DomainRuleException>(
                () => service.UploadProofAsync(world.OrgWide, Request(ManualProofKind.AlloggiatiReceipt, DayOf(1)), content, CancellationToken.None));
            Assert.Equal(ComplianceArchiveErrorCodes.ProofDayInvalid, future.Code);
            var unknown = await Assert.ThrowsAsync<DomainRuleException>(
                () => service.UploadProofAsync(world.OrgWide, Request((ManualProofKind)99, DayOf(-1)), content, CancellationToken.None));
            Assert.Equal(ComplianceArchiveErrorCodes.ProofKindInvalid, unknown.Code);
        }

        Assert.Equal(puts, storage.Puts.Count);
        Assert.Equal(3, await db.ManualComplianceProofs.CountAsync());
    }

    /// <summary>
    /// The export of the period is a ZIP with every proof and an <c>index.csv</c> (kind, property, day or month, source, received, kept until),
    /// built on request: nothing is stored. The dates are written with real values (a date format with a time on a date would throw), a proof
    /// whose file is gone stays in the index and says so, and an export beyond the limit is refused whole.
    /// </summary>
    [Fact]
    public async Task ComplianceArchive_ExportIsAZipWithTheIndex_NeverStored_Test()
    {
        var world = await World.SeedAsync();
        var storage = new MemoryStorage();
        var receipt = await AddProofAsync(world, world.Org.Id, world.Granted.Id, ManualProofKind.AlloggiatiReceipt, ArrivalDay, storage);
        var istat = await AddProofAsync(world, world.Org.Id, world.Granted.Id, ManualProofKind.IstatProof, new DateOnly(2026, 10, 1), storage);
        var lost = await AddProofAsync(world, world.Org.Id, world.Granted.Id, ManualProofKind.TouristTaxDeclaration, new DateOnly(2026, 10, 1), storage);
        storage.Forget(lost.FileKey);
        await using var db = world.Db();
        var service = NewService(world, db, storage, trafficLight: false);

        await using (var export = await service.ExportAsync(world.OrgWide, new ComplianceArchiveExportQuery(new DateOnly(2026, 10, 1), new DateOnly(2026, 11, 30), MoneyProofs: true), CancellationToken.None))
        {
            Assert.Equal(2, export.FileCount);
            Assert.Equal("prove-adempimenti-2026-10-01_2026-11-30.zip", export.FileName);
            using var zip = new ZipArchive(export.Content, ZipArchiveMode.Read);
            Assert.Equal(3, zip.Entries.Count);
            Assert.Contains(zip.Entries, e => e.FullName.StartsWith($"prove/2026-11-03_alloggiati_{receipt.Id.ToString("N")[..8]}", StringComparison.Ordinal));
            Assert.Contains(zip.Entries, e => e.FullName.StartsWith($"prove/2026-10-01_istat_{istat.Id.ToString("N")[..8]}", StringComparison.Ordinal));

            var index = ReadEntry(zip, "index.csv");
            var lines = index.TrimStart((char)0xFEFF).Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
            Assert.Equal("Tipo;Immobile;Giorno o periodo;Origine;Ricevuta il (UTC);Conservata fino al (UTC);File", lines[0]);
            Assert.Equal(4, lines.Length);
            Assert.Contains(
                lines,
                l => l.StartsWith("Ricevuta Alloggiati Web;Trullo;2026-11-03;Caricata dall'host;2026-11-06T10:00:00Z;2031-11-06T10:00:00Z;prove/", StringComparison.Ordinal));
            Assert.Contains(lines, l => l.StartsWith("Prova della comunicazione ISTAT;Trullo;2026-10;", StringComparison.Ordinal));
            Assert.Contains(lines, l => l.StartsWith("Ricevuta della dichiarazione dell'imposta di soggiorno;Trullo;2026-10;", StringComparison.Ordinal) && l.EndsWith(";file non disponibile", StringComparison.Ordinal));
        }

        Assert.Empty(storage.Puts);

        // With the traffic light on the pack also carries the coverage of the period, and nothing else changes.
        await using (var withCoverage = await NewService(world, db, storage).ExportAsync(
                         world.OrgWide, new ComplianceArchiveExportQuery(new DateOnly(2026, 10, 1), new DateOnly(2026, 11, 30), MoneyProofs: true), CancellationToken.None))
        {
            using var zip = new ZipArchive(withCoverage.Content, ZipArchiveMode.Read);
            Assert.Equal(4, zip.Entries.Count);
            Assert.StartsWith("Obbligo;Immobile;Soggiorno;Arrivo;Periodo;Esito;Origine;", ReadEntry(zip, ComplianceArchiveDocuments.CoverageFileName).TrimStart((char)0xFEFF), StringComparison.Ordinal);
        }

        // Beyond the limit: refused whole, before a file is read.
        world.Options.Archive.ExportMaxFiles = 2;
        var refused = await Assert.ThrowsAsync<DomainRuleException>(
            () => NewService(world, db, storage).ExportAsync(world.OrgWide, new ComplianceArchiveExportQuery(new DateOnly(2026, 10, 1), new DateOnly(2026, 11, 30), MoneyProofs: true), CancellationToken.None));
        Assert.Equal(ComplianceArchiveErrorCodes.ExportTooLarge, refused.Code);
    }

    // ─── Who may upload a tax proof ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// PROOF TEST: permission rule. The archive is read with <c>booking.read</c>; the upload asks <c>alloggiati.submit</c>, and the receipt of a tax
    /// declaration or payment (a record of money) asks <c>payment.write</c> besides: without it the answer is 403 and the service is never called.
    /// The report is behind the traffic light.
    /// </summary>
    [Fact]
    public async Task ComplianceArchive_UploadOfATaxProofNeedsPaymentWrite_AndTheReportTheTrafficLight_Test()
    {
        var controller = typeof(ComplianceArchiveController);
        Assert.Contains(CasazenPolicies.BookingRead, PoliciesOf(controller));
        var upload = controller.GetMethod(nameof(ComplianceArchiveController.Upload))!;
        Assert.Contains(CasazenPolicies.AlloggiatiSubmit, PoliciesOf(upload));
        foreach (var read in new[]
                 {
                     nameof(ComplianceArchiveController.List), nameof(ComplianceArchiveController.GetBookingCompliance),
                     nameof(ComplianceArchiveController.Download), nameof(ComplianceArchiveController.Export),
                     nameof(ComplianceArchiveController.GetReport), nameof(ComplianceArchiveController.ExportReport),
                 })
        {
            Assert.DoesNotContain(CasazenPolicies.AlloggiatiSubmit, PoliciesOf(controller.GetMethod(read)!));
        }

        foreach (var report in new[] { nameof(ComplianceArchiveController.GetReport), nameof(ComplianceArchiveController.ExportReport) })
        {
            Assert.Equal(
                [FeatureFlags.ComplianceTrafficLight],
                controller.GetMethod(report)!.GetCustomAttributes<FeatureGateAttribute>().Select(g => g.Flag));
        }

        Assert.True(ComplianceArchiveController.IsTaxProof(ManualProofKind.TouristTaxDeclaration));
        Assert.True(ComplianceArchiveController.IsTaxProof(ManualProofKind.TouristTaxPayment));
        Assert.False(ComplianceArchiveController.IsTaxProof(ManualProofKind.AlloggiatiReceipt));
        Assert.False(ComplianceArchiveController.IsTaxProof(ManualProofKind.IstatProof));

        // A caller without payment.write is refused for a tax proof before anything is read; an Alloggiati receipt does not ask for it.
        var orgId = Guid.NewGuid();
        var propertyId = Guid.NewGuid();
        var archive = new Mock<IComplianceArchiveService>(MockBehavior.Strict);
        var authorization = new Mock<IAuthorizationService>();
        authorization
            .Setup(a => a.AuthorizeAsync(It.IsAny<ClaimsPrincipal>(), It.IsAny<object?>(), CasazenPolicies.PaymentWrite))
            .ReturnsAsync(AuthorizationResult.Failed());
        var orgs = new Mock<IOrgContextResolver>();
        orgs.Setup(o => o.GetOrProvisionOrgIdAsync(It.IsAny<CancellationToken>())).ReturnsAsync(orgId);
        var scopes = new Mock<IHostScopeResolver>();
        scopes
            .Setup(s => s.ResolveAsync("auth0|host", It.IsAny<IReadOnlySet<string>>(), orgId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new HostScope(orgId));
        archive
            .Setup(a => a.UploadProofAsync(
                It.IsAny<HostScope>(), It.Is<UploadComplianceProofRequest>(r => r.Kind == ManualProofKind.AlloggiatiReceipt), It.IsAny<Stream>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ComplianceArchiveEntry(
                Guid.NewGuid(), ManualProofKind.AlloggiatiReceipt, propertyId, "Trullo", ArrivalDay, null, ComplianceEvidenceSources.ManualUpload, DateTime.UtcNow, DateTime.UtcNow.AddYears(5), "application/pdf"));

        var sut = new ComplianceArchiveController(archive.Object, orgs.Object, scopes.Object, authorization.Object, Options.Create(new ComplianceOptions()))
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", "auth0|host")], "test")),
                },
            },
        };
        var file = new Mock<IFormFile>();
        file.SetupGet(f => f.Length).Returns(PdfBytes.Length);
        file.SetupGet(f => f.FileName).Returns("ricevuta.pdf");
        file.Setup(f => f.OpenReadStream()).Returns(() => new MemoryStream(PdfBytes));

        foreach (var kind in new[] { ManualProofKind.TouristTaxDeclaration, ManualProofKind.TouristTaxPayment })
        {
            var forbidden = await sut.Upload(
                new UploadComplianceProofForm { PropertyId = propertyId, Kind = kind, Day = ArrivalDay, File = file.Object }, CancellationToken.None);
            Assert.IsType<ForbidResult>(forbidden);
        }

        var created = await sut.Upload(
            new UploadComplianceProofForm { PropertyId = propertyId, Kind = ManualProofKind.AlloggiatiReceipt, Day = ArrivalDay, File = file.Object },
            CancellationToken.None);
        Assert.Equal(StatusCodes.Status201Created, Assert.IsType<ObjectResult>(created).StatusCode);

        // With payment.write the receipt of a payment goes through to the service.
        authorization
            .Setup(a => a.AuthorizeAsync(It.IsAny<ClaimsPrincipal>(), It.IsAny<object?>(), CasazenPolicies.PaymentWrite))
            .ReturnsAsync(AuthorizationResult.Success());
        archive
            .Setup(a => a.UploadProofAsync(
                It.IsAny<HostScope>(), It.Is<UploadComplianceProofRequest>(r => r.Kind == ManualProofKind.TouristTaxPayment), It.IsAny<Stream>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ComplianceArchiveEntry(
                Guid.NewGuid(), ManualProofKind.TouristTaxPayment, propertyId, "Trullo", new DateOnly(2026, 10, 1), "2026-10", ComplianceEvidenceSources.ManualUpload, DateTime.UtcNow, DateTime.UtcNow.AddYears(5), "application/pdf"));
        var paid = await sut.Upload(
            new UploadComplianceProofForm { PropertyId = propertyId, Kind = ManualProofKind.TouristTaxPayment, Day = new DateOnly(2026, 10, 9), File = file.Object },
            CancellationToken.None);
        Assert.Equal(StatusCodes.Status201Created, Assert.IsType<ObjectResult>(paid).StatusCode);
    }

    // ─── Helpers ────────────────────────────────────────────────────────────────────────────────────────

    private static IEnumerable<string?> PoliciesOf(MemberInfo member) =>
        member.GetCustomAttributes<AuthorizeAttribute>(inherit: true).Select(a => a.Policy);

    private static ComplianceArchiveService NewService(World world, AppDbContext db, IFileStorage storage, bool trafficLight = true)
    {
        var flags = new Mock<IFeatureFlags>();
        flags.Setup(f => f.IsEnabled(FeatureFlags.ComplianceTrafficLight)).Returns(trafficLight);
        return new ComplianceArchiveService(
            db,
            storage,
            world.StatusService(db),
            new MigraDocPdfDocumentRenderer(),
            flags.Object,
            Options.Create(world.Options),
            NullLogger<ComplianceArchiveService>.Instance,
            world.Clock);
    }

    private static async Task<ManualComplianceProof> AddProofAsync(
        World world,
        Guid orgId,
        Guid propertyId,
        ManualProofKind kind,
        DateOnly day,
        MemoryStorage storage)
    {
        await using var db = world.Db();
        var id = Guid.NewGuid();
        var key = StorageKeys.ManualComplianceProof(orgId, id, $"{Guid.NewGuid():N}.pdf");
        storage.Put(key, PdfBytes);
        var proof = new ManualComplianceProof
        {
            Id = id,
            OrgId = orgId,
            PropertyId = propertyId,
            Kind = kind,
            Day = day,
            FileKey = key,
            ContentType = "application/pdf",
            UploadedByUserId = world.Owner.Id,
            UploadedAt = world.Clock.GetUtcNow().UtcDateTime,
        };
        db.ManualComplianceProofs.Add(proof);
        await db.SaveChangesAsync();
        return proof;
    }

    private static ComplianceSendLogEntry LogLine(Property property, Booking stay, ComplianceSender sender) => new()
    {
        OrgId = property.OrgId,
        PropertyId = property.Id,
        BookingId = stay.Id,
        Kind = ComplianceSendKind.AlloggiatiGuests,
        Sender = sender,
        SentAt = new DateTime(2026, 11, 3, 8, 0, 0, DateTimeKind.Utc),
    };

    private static string ReadEntry(ZipArchive zip, string name)
    {
        using var reader = new StreamReader(zip.GetEntry(name)!.Open(), Encoding.UTF8);
        return reader.ReadToEnd();
    }

    /// <summary>An in-memory private bucket. It refuses to give a public address: a proof must never have one.</summary>
    private sealed class MemoryStorage : IFileStorage
    {
        private readonly Dictionary<string, byte[]> _files = [];

        public bool Signs { get; set; }

        public List<string> Puts { get; } = [];

        public List<string> Opened { get; } = [];

        /// <summary>The keys a signed address was made for.</summary>
        public List<string> Signed { get; } = [];

        public StorageBucket LastBucket { get; private set; }

        public TimeSpan LastLifetime { get; private set; }

        public void Put(string key, byte[] content) => _files[key] = content;

        public void Forget(string key) => _files.Remove(key);

        public async Task PutAsync(StorageBucket bucket, string key, Stream content, string contentType, CancellationToken cancellationToken = default)
        {
            using var copy = new MemoryStream();
            await content.CopyToAsync(copy, cancellationToken);
            _files[key] = copy.ToArray();
            Puts.Add(key);
            LastBucket = bucket;
        }

        public Task<Stream?> OpenReadAsync(StorageBucket bucket, string key, CancellationToken cancellationToken = default)
        {
            Opened.Add(key);
            return Task.FromResult<Stream?>(_files.TryGetValue(key, out var bytes) ? new MemoryStream(bytes) : null);
        }

        public Task<bool> ExistsAsync(StorageBucket bucket, string key, CancellationToken cancellationToken = default) =>
            Task.FromResult(_files.ContainsKey(key));

        public Task DeleteAsync(StorageBucket bucket, string key, CancellationToken cancellationToken = default)
        {
            _files.Remove(key);
            return Task.CompletedTask;
        }

        public string GetPublicUrl(string key) => throw new InvalidOperationException("A compliance proof has no public address.");

        public string? TryGetPublicKey(string url) => null;

        public Task<Uri?> GetSignedReadUrlAsync(string key, TimeSpan lifetime, string? downloadFileName, CancellationToken cancellationToken = default)
        {
            LastLifetime = lifetime;
            Signed.Add(key);
            return Task.FromResult<Uri?>(Signs ? new Uri($"https://storage.example/private/{key}?signature=test") : null);
        }
    }
}
