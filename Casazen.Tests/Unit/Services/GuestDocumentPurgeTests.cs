using System.Text.Json;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Features;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Documents;
using Casazen.Infrastructure.Jobs;
using Casazen.Infrastructure.Services;
using Hangfire;
using Hangfire.Common;
using Hangfire.States;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;
using World = Casazen.Tests.Unit.Services.ComplianceStatusServiceTests.World;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// The documents of the guests are deleted once the stay is covered by an archived Alloggiati receipt (E20/F02/US01/T02, NF-121, NF-16, decision O4, step B11). One proof
/// for each rule that costs if it fails: only a stay with a receipt loses its documents and nothing personal is logged (privacy), the legal record of the guests stays
/// (data), the flag off deletes nothing (data), and another organization's stay is never touched (permission).
/// </summary>
public sealed class GuestDocumentPurgeTests
{
    private static readonly byte[] Bytes = [1, 2, 3, 4];

    private static readonly DateOnly Today = new(2026, 11, 6);

    private static DateOnly DayOf(int offsetFromToday) => Today.AddDays(offsetFromToday);

    // ─── Only a stay with a receipt ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// PROOF TEST: privacy rule. Only the stay covered by an archived receipt (its flat, its arrival day) loses its documents: the photos of its verification, the rows
    /// the verification already marked deleted and the legacy scan of its booker. A stay of another flat of the day, another day of the same flat, a cancelled stay and a
    /// stay only entrusted to the channel manager keep everything. One line of the send log for the stay that lost something, none for the others, nothing on a second
    /// run, and not a name, a document number or a storage key in any log line.
    /// </summary>
    [Fact]
    public async Task Purge_OnlyCoveredStays_Test()
    {
        var world = await World.SeedAsync();
        var storage = new RecordingStorage();
        var purgeLog = new CapturingLogger<GuestDocumentPurgeService>();
        var eraserLog = new CapturingLogger<GuestDataEraser>();
        var covered = await AddStayWithDocumentsAsync(world, storage, world.Granted, -3, images: 2, alreadyMarkedDeleted: 1, scan: true);
        var otherFlat = await AddStayWithDocumentsAsync(world, storage, world.Hidden, -3, images: 1, alreadyMarkedDeleted: 0, scan: true);
        var otherDay = await AddStayWithDocumentsAsync(world, storage, world.Granted, -2, images: 1, alreadyMarkedDeleted: 0, scan: true);
        var cancelled = await AddStayWithDocumentsAsync(world, storage, world.Granted, -3, images: 1, alreadyMarkedDeleted: 0, scan: true, BookingStatus.Cancelled);
        var entrusted = await AddStayWithDocumentsAsync(world, storage, world.Hidden, -4, images: 1, alreadyMarkedDeleted: 0, scan: true);
        await world.EntrustAsync(entrusted.Stay, [0, 1]);
        await AddReceiptAsync(world, world.Org.Id, world.Granted.Id, DayOf(-3));
        await using var db = world.Db();
        var service = NewService(world, db, storage, purgeLog: purgeLog, eraserLog: eraserLog);

        var result = await service.PurgeAsync(null, CancellationToken.None);

        Assert.Equal(new GuestDocumentPurgeResult(true, 1, 2, 1), result);
        Assert.Equal(covered.AllKeys.Order(StringComparer.Ordinal), storage.Deleted.Order(StringComparer.Ordinal));
        await using var read = world.Db();
        Assert.Empty(await read.DocumentImages.AsNoTracking().Where(i => i.BookingId == covered.Stay.Id).ToListAsync());
        Assert.Null((await read.Guests.AsNoTracking().SingleAsync(g => g.Id == covered.Stay.GuestId)).DocumentScanUrl);
        foreach (var kept in new[] { otherFlat, otherDay, cancelled, entrusted })
        {
            Assert.Equal(kept.ImageKeys.Length, await read.DocumentImages.AsNoTracking().CountAsync(i => i.BookingId == kept.Stay.Id));
            Assert.Equal(kept.ScanKey, (await read.Guests.AsNoTracking().SingleAsync(g => g.Id == kept.Stay.GuestId)).DocumentScanUrl);
            Assert.All(kept.AllKeys, key => Assert.True(storage.Has(key), "A stay without a receipt keeps its documents."));
        }

        // The log of the send: one line for the stay that lost something, with the date, by CasaZen, with no person and no reference.
        var line = Assert.Single(await read.ComplianceSendLogEntries.AsNoTracking().ToListAsync());
        Assert.Equal(
            (covered.Stay.Id, world.Granted.Id, world.Org.Id, ComplianceSendKind.DocumentsDeleted, ComplianceSender.CasaZen, world.Clock.GetUtcNow().UtcDateTime),
            (line.BookingId, line.PropertyId, line.OrgId, line.Kind, line.Sender, line.SentAt));
        Assert.Null(line.Reference);
        Assert.Null(line.ByUserId);

        // A second run finds nothing: nothing is deleted again, no second line.
        var deletedBefore = storage.Deleted.Count;
        var again = await NewService(world, world.Db(), storage).PurgeAsync(null, CancellationToken.None);
        Assert.Equal(new GuestDocumentPurgeResult(true, 0, 0, 0), again);
        Assert.Equal(deletedBefore, storage.Deleted.Count);
        Assert.Single(await read.ComplianceSendLogEntries.AsNoTracking().ToListAsync());

        // Nothing personal is logged: counts and ids, never the name of the guest, the number of the document or a key of the storage.
        var logged = string.Join('\n', purgeLog.Messages.Concat(eraserLog.Messages));
        Assert.NotEmpty(purgeLog.Messages);
        Assert.DoesNotContain("Verdi", logged, StringComparison.Ordinal);
        Assert.DoesNotContain("Anna", logged, StringComparison.Ordinal);
        Assert.DoesNotContain(LegalDocumentNumber, logged, StringComparison.Ordinal);
        Assert.DoesNotContain("guest-documents/", logged, StringComparison.Ordinal);
        Assert.All(covered.AllKeys, key => Assert.DoesNotContain(key, logged, StringComparison.Ordinal));
    }

    /// <summary>
    /// PROOF TEST: data rule. The document data of the guests of the stay (number, type, dates, place of issue, names) are the legal record of the communication and
    /// stay exactly as they were: the purge deletes the photos and the scan, never a field of a <c>StayGuest</c>, of the booker, or of the booking.
    /// </summary>
    [Fact]
    public async Task Purge_KeepsStayGuestLegalData_Test()
    {
        var world = await World.SeedAsync();
        var storage = new RecordingStorage();
        var covered = await AddStayWithDocumentsAsync(world, storage, world.Granted, -3, images: 2, alreadyMarkedDeleted: 0, scan: true);
        await AddReceiptAsync(world, world.Org.Id, world.Granted.Id, DayOf(-3));
        var before = await LegalRecordAsync(world, covered.Stay);

        await using (var db = world.Db())
            Assert.Equal(1, (await NewService(world, db, storage).PurgeAsync(null, CancellationToken.None)).Stays);

        var after = await LegalRecordAsync(world, covered.Stay);
        Assert.Equal(2, after.Guests.Count);
        Assert.All(after.Guests, guest => Assert.Equal(LegalDocumentNumber, guest.DocumentNumber));
        Assert.Equal(JsonSerializer.Serialize(before), JsonSerializer.Serialize(after));
        await using var read = world.Db();
        Assert.Empty(await read.DocumentImages.AsNoTracking().ToListAsync());
        Assert.Null((await read.Guests.AsNoTracking().SingleAsync(g => g.Id == covered.Stay.GuestId)).DocumentScanUrl);
    }

    /// <summary>
    /// PROOF TEST: data rule. With <c>Features:PurgeDocumentsAfterReceipt</c> off a run reads nothing and deletes nothing, even for a stay that has its receipt and its
    /// documents; the same data are deleted the moment it is on, so the flag is the one thing that decides. The jobs only run the service.
    /// </summary>
    [Fact]
    public async Task Purge_FlagOff_DeletesNothing_Test()
    {
        var world = await World.SeedAsync();
        var storage = new RecordingStorage();
        var covered = await AddStayWithDocumentsAsync(world, storage, world.Granted, -3, images: 2, alreadyMarkedDeleted: 0, scan: true);
        await AddReceiptAsync(world, world.Org.Id, world.Granted.Id, DayOf(-3));

        await using (var db = world.Db())
        {
            var off = await NewService(world, db, storage, on: false).PurgeAsync(null, CancellationToken.None);
            Assert.Equal(GuestDocumentPurgeResult.Off, off);
            Assert.False(off.Enabled);
            Assert.Equal(GuestDocumentPurgeResult.Off, await NewService(world, db, storage, on: false).PurgeAsync(world.Org.Id, CancellationToken.None));
        }

        Assert.Empty(storage.Deleted);
        Assert.Empty(storage.Opened);
        await using (var read = world.Db())
        {
            Assert.Equal(2, await read.DocumentImages.AsNoTracking().CountAsync());
            Assert.Equal(covered.ScanKey, (await read.Guests.AsNoTracking().SingleAsync(g => g.Id == covered.Stay.GuestId)).DocumentScanUrl);
            Assert.Empty(await read.ComplianceSendLogEntries.AsNoTracking().ToListAsync());
        }

        // The jobs only run the service: the daily one for every org, the one queued by an upload for the org it names.
        var service = new Mock<IGuestDocumentPurgeService>();
        service.Setup(s => s.PurgeAsync(It.IsAny<Guid?>(), It.IsAny<CancellationToken>())).ReturnsAsync(GuestDocumentPurgeResult.Off);
        var job = new GuestDocumentPurgeJob(service.Object);
        await job.ExecuteAsync(CancellationToken.None);
        await job.ExecuteForOrgAsync(world.Org.Id, CancellationToken.None);
        service.Verify(s => s.PurgeAsync(null, It.IsAny<CancellationToken>()), Times.Once);
        service.Verify(s => s.PurgeAsync(world.Org.Id, It.IsAny<CancellationToken>()), Times.Once);

        await using var enabled = world.Db();
        Assert.Equal(new GuestDocumentPurgeResult(true, 1, 2, 1), await NewService(world, enabled, storage).PurgeAsync(null, CancellationToken.None));
        Assert.Empty(await enabled.DocumentImages.AsNoTracking().ToListAsync());
    }

    /// <summary>
    /// PROOF TEST: permission rule. A run for one organization touches only that organization's stays, and a receipt of another organization never covers a stay (not even
    /// one filed, by mistake, under this organization's flat); the daily run, for every organization, deletes the others' own documents too.
    /// </summary>
    [Fact]
    public async Task Purge_OtherOrganizationUntouched_Test()
    {
        var world = await World.SeedAsync();
        var storage = new RecordingStorage();
        var own = await AddStayWithDocumentsAsync(world, storage, world.Granted, -3, images: 1, alreadyMarkedDeleted: 0, scan: true);
        var notCovered = await AddStayWithDocumentsAsync(world, storage, world.Hidden, -3, images: 1, alreadyMarkedDeleted: 0, scan: true);
        var foreign = await AddStayWithDocumentsAsync(world, storage, world.Foreign, -3, images: 1, alreadyMarkedDeleted: 0, scan: true);
        await AddReceiptAsync(world, world.Org.Id, world.Granted.Id, DayOf(-3));
        await AddReceiptAsync(world, world.Foreign.OrgId, world.Foreign.Id, DayOf(-3));

        // A receipt of the other organization written under a flat of this one: it covers nothing here, because it is not this organization's.
        await using (var db = world.Db())
        {
            db.ManualComplianceProofs.Add(new ManualComplianceProof
            {
                OrgId = world.Foreign.OrgId,
                PropertyId = world.Hidden.Id,
                Kind = ManualProofKind.AlloggiatiReceipt,
                Day = DayOf(-3),
                FileKey = $"compliance-proofs/{world.Foreign.OrgId}/{Guid.NewGuid()}/x.pdf",
                ContentType = "application/pdf",
                UploadedByUserId = world.Owner.Id,
                UploadedAt = world.Clock.GetUtcNow().UtcDateTime,
            });
            await db.SaveChangesAsync();
        }

        await using (var db = world.Db())
            Assert.Equal(new GuestDocumentPurgeResult(true, 1, 1, 1), await NewService(world, db, storage).PurgeAsync(world.Org.Id, CancellationToken.None));

        await using (var read = world.Db())
        {
            Assert.Empty(await read.DocumentImages.AsNoTracking().Where(i => i.BookingId == own.Stay.Id).ToListAsync());
            Assert.Equal(1, await read.DocumentImages.AsNoTracking().CountAsync(i => i.BookingId == notCovered.Stay.Id));
            Assert.Equal(1, await read.DocumentImages.AsNoTracking().CountAsync(i => i.BookingId == foreign.Stay.Id));
            Assert.Equal(foreign.ScanKey, (await read.Guests.AsNoTracking().SingleAsync(g => g.Id == foreign.Stay.GuestId)).DocumentScanUrl);
            Assert.Equal(notCovered.ScanKey, (await read.Guests.AsNoTracking().SingleAsync(g => g.Id == notCovered.Stay.GuestId)).DocumentScanUrl);
            Assert.All(foreign.AllKeys, key => Assert.True(storage.Has(key)));
            Assert.Equal(world.Org.Id, Assert.Single(await read.ComplianceSendLogEntries.AsNoTracking().ToListAsync()).OrgId);
        }

        // The daily run is over every organization: the other one's own receipt covers its own stay.
        await using (var db = world.Db())
            Assert.Equal(new GuestDocumentPurgeResult(true, 1, 1, 1), await NewService(world, db, storage).PurgeAsync(null, CancellationToken.None));

        await using var after = world.Db();
        Assert.Empty(await after.DocumentImages.AsNoTracking().Where(i => i.BookingId == foreign.Stay.Id).ToListAsync());
        Assert.Equal(1, await after.DocumentImages.AsNoTracking().CountAsync(i => i.BookingId == notCovered.Stay.Id));
    }

    // ─── The rest of the rule ───────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// PROOF TEST: privacy rule. The legacy scan belongs to the guest, not to one stay: while the guest has another stay that owes the communication and has no receipt
    /// yet, the scan stays (the photos of the covered stay still go); once that stay is covered too, the scan goes with its photos.
    /// </summary>
    [Fact]
    public async Task Purge_AScanWaitsForTheOtherStaysOfTheGuest_Test()
    {
        var world = await World.SeedAsync();
        var storage = new RecordingStorage();
        var first = await AddStayWithDocumentsAsync(world, storage, world.Granted, -3, images: 1, alreadyMarkedDeleted: 0, scan: true);
        var second = await AddSecondStayOfTheSameGuestAsync(world, storage, first.Stay, world.Hidden, -1);
        await AddReceiptAsync(world, world.Org.Id, world.Granted.Id, DayOf(-3));

        await using (var db = world.Db())
            Assert.Equal(new GuestDocumentPurgeResult(true, 1, 1, 0), await NewService(world, db, storage).PurgeAsync(null, CancellationToken.None));

        await using (var read = world.Db())
        {
            Assert.Equal(first.ScanKey, (await read.Guests.AsNoTracking().SingleAsync(g => g.Id == first.Stay.GuestId)).DocumentScanUrl);
            Assert.True(storage.Has(first.ScanKey!));
            Assert.Equal(1, await read.DocumentImages.AsNoTracking().CountAsync(i => i.BookingId == second.Id));
        }

        // The second stay is covered now: its photo goes, and the first stay (whose photos went already) takes the scan, which nothing waits for any more.
        await AddReceiptAsync(world, world.Org.Id, world.Hidden.Id, DayOf(-1));
        await using (var db = world.Db())
            Assert.Equal(new GuestDocumentPurgeResult(true, 2, 1, 1), await NewService(world, db, storage).PurgeAsync(null, CancellationToken.None));

        await using var after = world.Db();
        Assert.Null((await after.Guests.AsNoTracking().SingleAsync(g => g.Id == first.Stay.GuestId)).DocumentScanUrl);
        Assert.False(storage.Has(first.ScanKey!));
        Assert.Empty(await after.DocumentImages.AsNoTracking().ToListAsync());
    }

    /// <summary>
    /// PROOF TEST: privacy rule. «Covered» is the verdict of the traffic light, not only a receipt on the flat and the day: a stay whose communication failed (an error, a
    /// rejection, a guest the channel manager could not take) or was only partly accepted is red whatever receipt exists, so it is not covered and keeps its photos and its scan,
    /// one by one for each of the four states; the stays of the same flat and day with nothing wrong are purged, and once the failure is mended the next run deletes the rest.
    /// </summary>
    [Fact]
    public async Task Purge_AStayWhoseCommunicationFailedOrWasPartlyAccepted_IsNotCovered_AndKeepsItsDocuments_Test()
    {
        var world = await World.SeedAsync();
        world.Senders[world.Granted.Id] = ComplianceSenderType.ChannelManager;
        var storage = new RecordingStorage();
        var error = await AddStayWithDocumentsAsync(world, storage, world.Hidden, -3, images: 1, alreadyMarkedDeleted: 0, scan: true, report: AlloggiatiWebStatus.Errore);
        var rejected = await AddStayWithDocumentsAsync(world, storage, world.Hidden, -3, images: 1, alreadyMarkedDeleted: 0, scan: true, report: AlloggiatiWebStatus.Rifiutato);
        var partly = await AddStayWithDocumentsAsync(world, storage, world.Hidden, -3, images: 1, alreadyMarkedDeleted: 0, scan: true, report: AlloggiatiWebStatus.PartiallyAccepted);
        var writeFailed = await AddStayWithDocumentsAsync(world, storage, world.Granted, -3, images: 1, alreadyMarkedDeleted: 0, scan: true);
        await world.EntrustAsync(writeFailed.Stay, [0], status: OctorateSyncStatus.Failed);
        var fineOfTheFlat = await AddStayWithDocumentsAsync(world, storage, world.Hidden, -3, images: 1, alreadyMarkedDeleted: 0, scan: true);
        var fineOfTheChannelManager = await AddStayWithDocumentsAsync(world, storage, world.Granted, -3, images: 1, alreadyMarkedDeleted: 0, scan: true);
        await AddReceiptAsync(world, world.Org.Id, world.Hidden.Id, DayOf(-3));
        await AddReceiptAsync(world, world.Org.Id, world.Granted.Id, DayOf(-3));

        await using (var db = world.Db())
            Assert.Equal(new GuestDocumentPurgeResult(true, 2, 2, 2), await NewService(world, db, storage).PurgeAsync(null, CancellationToken.None));

        await using (var read = world.Db())
        {
            foreach (var kept in new[] { error, rejected, partly, writeFailed })
            {
                Assert.Equal(1, await read.DocumentImages.AsNoTracking().CountAsync(i => i.BookingId == kept.Stay.Id));
                Assert.Equal(kept.ScanKey, (await read.Guests.AsNoTracking().SingleAsync(g => g.Id == kept.Stay.GuestId)).DocumentScanUrl);
                Assert.All(kept.AllKeys, key => Assert.True(storage.Has(key), "A stay whose communication failed keeps its documents."));
            }

            foreach (var gone in new[] { fineOfTheFlat, fineOfTheChannelManager })
            {
                Assert.Empty(await read.DocumentImages.AsNoTracking().Where(i => i.BookingId == gone.Stay.Id).ToListAsync());
                Assert.Null((await read.Guests.AsNoTracking().SingleAsync(g => g.Id == gone.Stay.GuestId)).DocumentScanUrl);
            }

            Assert.Equal(
                new[] { fineOfTheFlat.Stay.Id, fineOfTheChannelManager.Stay.Id }.Order(),
                (await read.ComplianceSendLogEntries.AsNoTracking().ToListAsync()).Select(l => l.BookingId!.Value).Order());
        }

        // The communication of the stay in error is mended (declared sent): the traffic light reads it green with the receipt, and the next run deletes its documents.
        await using (var edit = world.Db())
        {
            (await edit.AlloggiatiWebReports.SingleAsync(r => r.BookingId == error.Stay.Id)).Status = AlloggiatiWebStatus.InviatoManualmente;
            await edit.SaveChangesAsync();
        }

        await using (var db = world.Db())
            Assert.Equal(new GuestDocumentPurgeResult(true, 1, 1, 1), await NewService(world, db, storage).PurgeAsync(null, CancellationToken.None));

        await using var after = world.Db();
        Assert.Empty(await after.DocumentImages.AsNoTracking().Where(i => i.BookingId == error.Stay.Id).ToListAsync());
        foreach (var stillKept in new[] { rejected, partly, writeFailed })
            Assert.Equal(1, await after.DocumentImages.AsNoTracking().CountAsync(i => i.BookingId == stillKept.Stay.Id));
    }

    /// <summary>
    /// PROOF TEST: privacy rule. Only what really left the storage is counted and written as a deletion: a scan whose object another guest record still points to (the
    /// snapshot of the same guest another stay was made with) loses the reference of the covered stay, which is saved, but it is neither counted nor written in the send log
    /// and its object stays; when the last record that points to it is cleared the object is deleted, counted and written.
    /// </summary>
    [Fact]
    public async Task Purge_AScanStillPointedToByAnotherRecord_LosesItsReference_ButIsNeitherCountedNorLogged_Test()
    {
        var world = await World.SeedAsync();
        var storage = new RecordingStorage();
        var first = await AddStayWithDocumentsAsync(world, storage, world.Granted, -3, images: 0, alreadyMarkedDeleted: 0, scan: true);
        var other = await AddStayOfASnapshotOfTheGuestAsync(world, first.Stay, world.Hidden, -1);
        await AddReceiptAsync(world, world.Org.Id, world.Granted.Id, DayOf(-3));

        // The first stay is covered: its record is cleared and saved, the object is the snapshot's too and stays; nothing is counted or written.
        await using (var db = world.Db())
            Assert.Equal(new GuestDocumentPurgeResult(true, 0, 0, 0), await NewService(world, db, storage).PurgeAsync(null, CancellationToken.None));

        await using (var read = world.Db())
        {
            Assert.Null((await read.Guests.AsNoTracking().SingleAsync(g => g.Id == first.Stay.GuestId)).DocumentScanUrl);
            Assert.Equal(first.ScanKey, (await read.Guests.AsNoTracking().SingleAsync(g => g.Id == other.GuestId)).DocumentScanUrl);
            Assert.Empty(await read.ComplianceSendLogEntries.AsNoTracking().ToListAsync());
        }

        Assert.True(storage.Has(first.ScanKey!));
        Assert.Empty(storage.Deleted);

        // The other stay is covered too: the last reference goes and the object with it, counted and written for that stay.
        await AddReceiptAsync(world, world.Org.Id, world.Hidden.Id, DayOf(-1));
        await using (var db = world.Db())
            Assert.Equal(new GuestDocumentPurgeResult(true, 1, 0, 1), await NewService(world, db, storage).PurgeAsync(null, CancellationToken.None));

        await using var after = world.Db();
        Assert.Null((await after.Guests.AsNoTracking().SingleAsync(g => g.Id == other.GuestId)).DocumentScanUrl);
        Assert.False(storage.Has(first.ScanKey!));
        Assert.Equal(other.Id, Assert.Single(await after.ComplianceSendLogEntries.AsNoTracking().ToListAsync()).BookingId);
    }

    /// <summary>
    /// PROOF TEST: data rule. A document the storage could not delete keeps its row and its reference (and no line of the send log), the other stays still go, and the run
    /// fails at its end instead of reading as done; once the storage answers again the stay is cleared.
    /// </summary>
    [Fact]
    public async Task Purge_AFileThatCannotBeDeleted_KeepsItsRowsAndFailsTheRun_Test()
    {
        var world = await World.SeedAsync();
        var storage = new RecordingStorage();
        var stuck = await AddStayWithDocumentsAsync(world, storage, world.Granted, -4, images: 1, alreadyMarkedDeleted: 0, scan: true);
        var fine = await AddStayWithDocumentsAsync(world, storage, world.Granted, -3, images: 1, alreadyMarkedDeleted: 0, scan: true);
        await AddReceiptAsync(world, world.Org.Id, world.Granted.Id, DayOf(-4));
        await AddReceiptAsync(world, world.Org.Id, world.Granted.Id, DayOf(-3));
        storage.FailOnDelete.Add(stuck.ImageKeys[0]);

        await using (var db = world.Db())
        {
            var failure = await Assert.ThrowsAsync<InvalidOperationException>(
                () => NewService(world, db, storage).PurgeAsync(null, CancellationToken.None));
            Assert.Contains("1 stay", failure.Message, StringComparison.Ordinal);
        }

        await using (var read = world.Db())
        {
            Assert.Equal(1, await read.DocumentImages.AsNoTracking().CountAsync(i => i.BookingId == stuck.Stay.Id));
            Assert.Equal(stuck.ScanKey, (await read.Guests.AsNoTracking().SingleAsync(g => g.Id == stuck.Stay.GuestId)).DocumentScanUrl);
            Assert.Empty(await read.DocumentImages.AsNoTracking().Where(i => i.BookingId == fine.Stay.Id).ToListAsync());
            Assert.Equal(fine.Stay.Id, Assert.Single(await read.ComplianceSendLogEntries.AsNoTracking().ToListAsync()).BookingId);
        }

        storage.FailOnDelete.Clear();
        await using (var db = world.Db())
            Assert.Equal(new GuestDocumentPurgeResult(true, 1, 1, 1), await NewService(world, db, storage).PurgeAsync(null, CancellationToken.None));

        await using var after = world.Db();
        Assert.Empty(await after.DocumentImages.AsNoTracking().ToListAsync());
        Assert.Equal(2, await after.ComplianceSendLogEntries.AsNoTracking().CountAsync());
    }

    /// <summary>
    /// The run after a receipt upload is queued, not made in the request: an Alloggiati receipt queues one run for the org of the caller while the flag is on; another kind of
    /// proof, or the flag off, queues nothing; and a queue that does not answer never fails the upload (the daily run does the work).
    /// </summary>
    [Fact]
    public async Task Purge_AfterAReceiptUpload_IsQueuedForTheOrg_NeverRunInTheRequest_Test()
    {
        var world = await World.SeedAsync();
        var storage = new RecordingStorage();
        var jobs = new Mock<IBackgroundJobClient>();
        jobs.Setup(j => j.Create(It.IsAny<Job>(), It.IsAny<IState>())).Returns("job-1");

        await using var db = world.Db();
        var service = NewArchiveService(world, db, storage, jobs.Object, purgeOn: true);
        await UploadAsync(service, world, ManualProofKind.AlloggiatiReceipt);
        jobs.Verify(
            j => j.Create(
                It.Is<Job>(job => job.Type == typeof(GuestDocumentPurgeJob)
                                  && job.Method.Name == nameof(GuestDocumentPurgeJob.ExecuteForOrgAsync)
                                  && (Guid)job.Args[0] == world.Org.Id),
                It.IsAny<IState>()),
            Times.Once);

        // Another kind of proof proves no stay of the day: nothing is queued.
        await UploadAsync(service, world, ManualProofKind.IstatProof);
        jobs.Verify(j => j.Create(It.IsAny<Job>(), It.IsAny<IState>()), Times.Once);

        // With the flag off nothing is queued.
        var off = NewArchiveService(world, db, storage, jobs.Object, purgeOn: false);
        await UploadAsync(off, world, ManualProofKind.AlloggiatiReceipt);
        jobs.Verify(j => j.Create(It.IsAny<Job>(), It.IsAny<IState>()), Times.Once);

        // A queue that is down: the proof is stored and the upload answers.
        var broken = new Mock<IBackgroundJobClient>();
        broken.Setup(j => j.Create(It.IsAny<Job>(), It.IsAny<IState>())).Throws(new InvalidOperationException("The queue is down."));
        var entry = await UploadAsync(NewArchiveService(world, db, storage, broken.Object, purgeOn: true), world, ManualProofKind.AlloggiatiReceipt);
        Assert.Contains(await db.ManualComplianceProofs.AsNoTracking().Select(p => p.Id).ToListAsync(), id => id == entry.Id);
    }

    // ─── Helpers ────────────────────────────────────────────────────────────────────────────────────────

    private const string LegalDocumentNumber = "XA1234567";

    /// <summary>A stay with its booker and documents: the keys of the photos, the key of the scan and all of them.</summary>
    private sealed record Seeded(Booking Stay, string[] ImageKeys, string? ScanKey)
    {
        public IEnumerable<string> AllKeys => ScanKey is null ? ImageKeys : [.. ImageKeys, ScanKey];
    }

    private static GuestDocumentPurgeService NewService(
        World world,
        AppDbContext db,
        RecordingStorage storage,
        bool on = true,
        CapturingLogger<GuestDocumentPurgeService>? purgeLog = null,
        CapturingLogger<GuestDataEraser>? eraserLog = null)
    {
        var flags = new Mock<IFeatureFlags>();
        flags.Setup(f => f.IsEnabled(FeatureFlags.PurgeDocumentsAfterReceipt)).Returns(on);
        return new GuestDocumentPurgeService(
            db,
            storage,
            new GuestDataEraser(db, storage, eraserLog ?? (ILogger<GuestDataEraser>)NullLogger<GuestDataEraser>.Instance),
            world.StatusService(db),
            flags.Object,
            world.Clock,
            purgeLog ?? (ILogger<GuestDocumentPurgeService>)NullLogger<GuestDocumentPurgeService>.Instance);
    }

    private static ComplianceArchiveService NewArchiveService(World world, AppDbContext db, RecordingStorage storage, IBackgroundJobClient jobs, bool purgeOn)
    {
        var flags = new Mock<IFeatureFlags>();
        flags.Setup(f => f.IsEnabled(FeatureFlags.PurgeDocumentsAfterReceipt)).Returns(purgeOn);
        return new ComplianceArchiveService(
            db,
            storage,
            world.StatusService(db),
            new MigraDocPdfDocumentRenderer(),
            flags.Object,
            Options.Create(world.Options),
            NullLogger<ComplianceArchiveService>.Instance,
            world.Clock,
            jobs);
    }

    private static async Task<ComplianceArchiveEntry> UploadAsync(ComplianceArchiveService service, World world, ManualProofKind kind)
    {
        await using var content = new MemoryStream("%PDF-1.4 TEST"u8.ToArray());
        return await service.UploadProofAsync(
            world.OrgWide,
            new UploadComplianceProofRequest(world.Granted.Id, kind, DayOf(-3), world.Owner.Id),
            content,
            CancellationToken.None);
    }

    /// <summary>
    /// A stay of <paramref name="property"/> with two registered guests that carry the legal document data, a booker with a legacy scan when asked, and
    /// <paramref name="images"/> photos of a verification (the first <paramref name="alreadyMarkedDeleted"/> of them already deleted from the bucket and marked by the
    /// verification, as the real flow leaves them).
    /// </summary>
    private static async Task<Seeded> AddStayWithDocumentsAsync(
        World world,
        RecordingStorage storage,
        Property property,
        int checkInOffset,
        int images,
        int alreadyMarkedDeleted,
        bool scan,
        BookingStatus status = BookingStatus.Confirmed,
        AlloggiatiWebStatus? report = null)
    {
        var (stay, _) = await world.AddStayAsync(property, checkInOffset, guests: 2, status: status, report: report);
        await using var db = world.Db();
        foreach (var guest in await db.StayGuests.Where(g => g.BookingId == stay.Id).ToListAsync())
        {
            guest.DocumentNumber = LegalDocumentNumber;
            guest.DocumentTypeCode = "IDENT";
            guest.DocumentIssuePlaceName = "Questura di Roma";
            guest.DocumentIssuedOn = new DateOnly(2020, 1, 2);
            guest.DocumentExpiresOn = new DateOnly(2030, 1, 2);
            guest.DateOfBirth = new DateTime(1980, 5, 6, 0, 0, 0, DateTimeKind.Utc);
            guest.CitizenshipName = "Italia";
        }

        var keys = new List<string>();
        for (var index = 0; index < images; index++)
        {
            var key = StorageKeys.GuestDocument(stay.OrgId, stay.GuestId, $"{Guid.NewGuid():N}.bin");
            var marked = index < alreadyMarkedDeleted;
            if (!marked)
                storage.Put(key);

            keys.Add(key);
            db.DocumentImages.Add(new DocumentImage
            {
                OrgId = stay.OrgId,
                GuestId = stay.GuestId,
                BookingId = stay.Id,
                FrontImageUrl = key,
                UploadedAt = world.Clock.GetUtcNow().UtcDateTime.AddDays(checkInOffset),
                ProcessedAt = world.Clock.GetUtcNow().UtcDateTime.AddDays(checkInOffset),
                DeletedAt = marked ? world.Clock.GetUtcNow().UtcDateTime.AddDays(checkInOffset) : null,
            });
        }

        string? scanKey = null;
        if (scan)
        {
            scanKey = StorageKeys.GuestDocument(stay.OrgId, stay.GuestId, "scansione.jpg");
            storage.Put(scanKey);
            (await db.Guests.SingleAsync(g => g.Id == stay.GuestId)).DocumentScanUrl = scanKey;
        }

        await db.SaveChangesAsync();
        return new Seeded(stay, [.. keys], scanKey);
    }

    /// <summary>A second stay of the booker of <paramref name="first"/> (the same guest, with its scan) in <paramref name="property"/>, with a photo of a verification.</summary>
    private static async Task<Booking> AddSecondStayOfTheSameGuestAsync(World world, RecordingStorage storage, Booking first, Property property, int checkInOffset)
    {
        await using var db = world.Db();
        var day = new DateTime(2026, 11, 6, 0, 0, 0, DateTimeKind.Utc).AddDays(checkInOffset);
        var stay = new Booking
        {
            OrgId = first.OrgId,
            PropertyId = property.Id,
            GuestId = first.GuestId,
            CheckInDate = day,
            CheckOutDate = day.AddDays(2),
            NumberOfGuests = 1,
            Status = BookingStatus.Confirmed,
            Source = BookingSource.Manual,
            BasePrice = 300m,
            TotalPrice = 300m,
        };
        var key = StorageKeys.GuestDocument(first.OrgId, first.GuestId, $"{Guid.NewGuid():N}.bin");
        storage.Put(key);
        db.Bookings.Add(stay);
        db.DocumentImages.Add(new DocumentImage { OrgId = first.OrgId, GuestId = first.GuestId, BookingId = stay.Id, FrontImageUrl = key });
        await db.SaveChangesAsync();
        return stay;
    }

    /// <summary>
    /// A stay in <paramref name="property"/> of ANOTHER guest record that is a snapshot of the booker of <paramref name="first"/> (what a check-in makes when the guest is shared by
    /// two stays): the copy carries the same reference to the scan, so the object is pointed to by two records.
    /// </summary>
    private static async Task<Booking> AddStayOfASnapshotOfTheGuestAsync(World world, Booking first, Property property, int checkInOffset)
    {
        await using var db = world.Db();
        var booker = await db.Guests.AsNoTracking().SingleAsync(g => g.Id == first.GuestId);
        var snapshot = booker.CreateSnapshot(world.Clock.GetUtcNow().UtcDateTime);
        var day = new DateTime(2026, 11, 6, 0, 0, 0, DateTimeKind.Utc).AddDays(checkInOffset);
        var stay = new Booking
        {
            OrgId = first.OrgId,
            PropertyId = property.Id,
            GuestId = snapshot.Id,
            CheckInDate = day,
            CheckOutDate = day.AddDays(2),
            NumberOfGuests = 1,
            Status = BookingStatus.Confirmed,
            Source = BookingSource.Manual,
            BasePrice = 300m,
            TotalPrice = 300m,
        };
        db.Guests.Add(snapshot);
        db.Bookings.Add(stay);
        await db.SaveChangesAsync();
        return stay;
    }

    /// <summary>An Alloggiati receipt of an org that covers <paramref name="propertyId"/> on <paramref name="day"/>, with its line of the flats it covers.</summary>
    private static async Task AddReceiptAsync(World world, Guid orgId, Guid propertyId, DateOnly day)
    {
        await using var db = world.Db();
        var id = Guid.NewGuid();
        db.ManualComplianceProofs.Add(new ManualComplianceProof
        {
            Id = id,
            OrgId = orgId,
            PropertyId = propertyId,
            Kind = ManualProofKind.AlloggiatiReceipt,
            Day = day,
            FileKey = StorageKeys.ManualComplianceProof(orgId, id, $"{Guid.NewGuid():N}.pdf"),
            ContentType = "application/pdf",
            UploadedByUserId = world.Owner.Id,
            UploadedAt = world.Clock.GetUtcNow().UtcDateTime,
        });
        db.ManualComplianceProofProperties.Add(new ManualComplianceProofProperty { OrgId = orgId, ProofId = id, PropertyId = propertyId });
        await db.SaveChangesAsync();
    }

    private sealed record GuestRecord(
        string FirstName,
        string LastName,
        string DocumentNumber,
        string? DocumentTypeCode,
        string DocumentIssuePlaceName,
        DateOnly? DocumentIssuedOn,
        DateOnly? DocumentExpiresOn,
        DateTime? DateOfBirth,
        string CitizenshipName,
        DateTime? AnonymizedAt);

    private sealed record LegalRecord(
        IReadOnlyList<GuestRecord> Guests,
        string BookerFirstName,
        string BookerEmail,
        BookingStatus Status,
        DateTime CheckInDate,
        decimal TotalPrice);

    /// <summary>What the guests of a stay and its booking hold, apart from the documents the purge deletes.</summary>
    private static async Task<LegalRecord> LegalRecordAsync(World world, Booking stay)
    {
        await using var db = world.Db();
        var guests = await db.StayGuests.AsNoTracking()
            .Where(g => g.BookingId == stay.Id)
            .OrderBy(g => g.Position)
            .Select(g => new GuestRecord(
                g.FirstName,
                g.LastName,
                g.DocumentNumber,
                g.DocumentTypeCode,
                g.DocumentIssuePlaceName,
                g.DocumentIssuedOn,
                g.DocumentExpiresOn,
                g.DateOfBirth,
                g.CitizenshipName,
                g.AnonymizedAt))
            .ToListAsync();
        var booking = await db.Bookings.AsNoTracking().SingleAsync(b => b.Id == stay.Id);
        var booker = await db.Guests.AsNoTracking().SingleAsync(g => g.Id == booking.GuestId);
        return new LegalRecord(guests, booker.FirstName, booker.Email, booking.Status, booking.CheckInDate, booking.TotalPrice);
    }

    /// <summary>A logger that keeps every line it is given (and the exception text), to prove what is not in it.</summary>
    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            Messages.Add(formatter(state, exception));
            if (exception is not null)
                Messages.Add(exception.ToString());
        }
    }

    /// <summary>A private bucket that remembers what was put, opened and deleted, and can refuse to delete some keys.</summary>
    private sealed class RecordingStorage : IFileStorage
    {
        private readonly Dictionary<string, byte[]> _files = [];

        public List<string> Opened { get; } = [];

        public List<string> Deleted { get; } = [];

        public HashSet<string> FailOnDelete { get; } = [];

        public void Put(string key) => _files[key] = Bytes;

        public bool Has(string key) => _files.ContainsKey(key);

        public async Task PutAsync(StorageBucket bucket, string key, Stream content, string contentType, CancellationToken cancellationToken = default)
        {
            using var copy = new MemoryStream();
            await content.CopyToAsync(copy, cancellationToken);
            _files[key] = copy.ToArray();
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
            if (FailOnDelete.Contains(key))
                throw new IOException("The storage did not answer.");

            _files.Remove(key);
            Deleted.Add(key);
            return Task.CompletedTask;
        }

        public string GetPublicUrl(string key) => throw new InvalidOperationException("A document of a guest has no public address.");

        public string? TryGetPublicKey(string url) => null;

        public Task<Uri?> GetSignedReadUrlAsync(string key, TimeSpan lifetime, string? downloadFileName, CancellationToken cancellationToken = default) =>
            Task.FromResult<Uri?>(null);
    }
}
