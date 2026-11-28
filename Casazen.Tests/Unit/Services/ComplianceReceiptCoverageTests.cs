using System.Text;
using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Features;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Documents;
using Casazen.Infrastructure.Jobs;
using Casazen.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;
using World = Casazen.Tests.Unit.Services.ComplianceStatusServiceTests.World;

namespace Casazen.Tests.Unit.Services;

public sealed class ComplianceReceiptCoverageTests
{
    private static readonly byte[] PdfBytes = Encoding.ASCII.GetBytes("%PDF-1.4 TEST RECEIPT");

    private static readonly DateOnly Today = new(2026, 11, 6);

    private static DateOnly DayOf(int offsetFromToday) => Today.AddDays(offsetFromToday);

    /// <summary>
    /// PROOF TEST: permission rule. A receipt names only flats of the caller's org that the caller reaches, in Italy: another org's flat, one out of the reach of a
    /// collaborator «Solo alcuni» and one that does not exist are the same 404 whichever of them is the flat of the request or another one it covers; a list for
    /// a kind of proof that covers one flat only, and a flat outside Italy among several, are the 422 of the coverage. Nothing is stored by any of them, and no
    /// list (or an empty one) is the flat of the request alone, never every flat of the org.
    /// </summary>
    [Fact]
    public async Task ReceiptCoverage_OtherOrganizationPropertyRefused_Test()
    {
        var world = await World.SeedAsync();
        var swiss = await AddFlatAsync(world, "Chalet", "CH");
        var storage = new RecordingStorage();
        await using var db = world.Db();
        var service = NewService(world, db, storage);

        foreach (var (scope, anchor, others) in new[]
        {
            (world.OrgWide, world.Granted.Id, new[] { world.Foreign.Id }),
            (world.OrgWide, world.Foreign.Id, Array.Empty<Guid>()),
            (world.OrgWide, world.Granted.Id, new[] { world.Hidden.Id, Guid.NewGuid() }),
            (world.Restricted, world.Granted.Id, new[] { world.Hidden.Id }),
            (world.Restricted, world.Hidden.Id, Array.Empty<Guid>()),
        })
        {
            await Assert.ThrowsAsync<NotFoundException>(() => UploadAsync(service, scope, world, anchor, DayOf(-3), others));
        }

        await Assert.ThrowsAsync<NotFoundException>(
            () => service.GetReceiptCoverageAsync(world.OrgWide, DayOf(-3), [world.Granted.Id, world.Foreign.Id], CancellationToken.None));
        await Assert.ThrowsAsync<NotFoundException>(
            () => service.GetReceiptCoverageAsync(world.Restricted, DayOf(-3), [world.Hidden.Id], CancellationToken.None));

        // A list is for the receipt of the Alloggiati communication only, and the flats of one receipt are all in Italy.
        var forAnotherKind = await Assert.ThrowsAsync<DomainRuleException>(() => UploadAsync(
            service, world.OrgWide, world, world.Granted.Id, DayOf(-3), [world.Hidden.Id], ManualProofKind.IstatProof));
        var abroad = await Assert.ThrowsAsync<DomainRuleException>(
            () => UploadAsync(service, world.OrgWide, world, world.Granted.Id, DayOf(-3), swiss.Id));
        var tooMany = await Assert.ThrowsAsync<DomainRuleException>(() => UploadAsync(
            service, world.OrgWide, world, world.Granted.Id, DayOf(-3), Enumerable.Range(0, ComplianceArchiveLimits.MaxCoveredProperties + 1).Select(_ => Guid.NewGuid()).ToArray()));
        Assert.All(
            new[] { forAnotherKind, abroad, tooMany },
            refused => Assert.Equal(ComplianceArchiveErrorCodes.ProofCoverageInvalid, refused.Code));
        Assert.Empty(storage.Puts);
        Assert.Empty(await db.ManualComplianceProofs.ToListAsync());
        Assert.Empty(await db.ManualComplianceProofProperties.ToListAsync());

        var empty = await Assert.ThrowsAsync<DomainRuleException>(
            () => service.GetReceiptCoverageAsync(world.OrgWide, DayOf(-3), [], CancellationToken.None));
        Assert.Equal(ComplianceArchiveErrorCodes.ProofCoverageInvalid, empty.Code);

        var alone = await UploadAsync(service, world.OrgWide, world, world.Granted.Id, DayOf(-3));
        var repeated = await UploadAsync(service, world.OrgWide, world, world.Granted.Id, DayOf(-3), world.Granted.Id, world.Granted.Id);
        Assert.Equal([world.Granted.Id], alone.CoveredProperties!.Select(flat => flat.Id));
        Assert.Equal([world.Granted.Id], repeated.CoveredProperties!.Select(flat => flat.Id));
        var lines = await db.ManualComplianceProofProperties.AsNoTracking().ToListAsync();
        Assert.Equal(2, lines.Count);
        Assert.All(lines, line => Assert.Equal(world.Granted.Id, line.PropertyId));
        Assert.Equal(2, storage.Puts.Count);
    }

    [Fact]
    public async Task ReceiptCoverage_AProofOfFlatsNotAllInReach_IsNotReadByACollaborator_Test()
    {
        var world = await World.SeedAsync();
        var storage = new RecordingStorage();
        await using var db = world.Db();
        var service = NewService(world, db, storage);
        var (grantedStay, _) = await world.AddStayAsync(world.Granted, checkInOffset: -3);
        var shared = await UploadAsync(service, world.OrgWide, world, world.Granted.Id, DayOf(-3), world.Hidden.Id);
        var alone = await UploadAsync(service, world.OrgWide, world, world.Granted.Id, DayOf(-3));

        var all = await service.ListAsync(world.OrgWide, new ComplianceArchiveQuery(), CancellationToken.None);
        Assert.Equal(new[] { shared.Id, alone.Id }.Order(), all.Items.Select(i => i.Id).Order());
        var sharedRow = Assert.Single(all.Items, i => i.Id == shared.Id);
        Assert.Equal([world.Granted.Id, world.Hidden.Id], sharedRow.CoveredProperties!.Select(flat => flat.Id));
        Assert.Equal(["Trullo", "Casa Bianca"], sharedRow.CoveredProperties!.Select(flat => flat.Name));
        var byTheOtherFlat = await service.ListAsync(world.OrgWide, new ComplianceArchiveQuery(PropertyId: world.Hidden.Id), CancellationToken.None);
        Assert.Equal([shared.Id], byTheOtherFlat.Items.Select(i => i.Id));

        var limited = await service.ListAsync(world.Restricted, new ComplianceArchiveQuery(), CancellationToken.None);
        Assert.Equal([alone.Id], limited.Items.Select(i => i.Id));
        Assert.Equal(1, limited.TotalCount);
        var refused = await Assert.ThrowsAsync<NotFoundException>(
            () => service.OpenDownloadAsync(world.Restricted, shared.Id, moneyProofs: true, CancellationToken.None));
        Assert.Equal(ComplianceArchiveErrorCodes.ProofNotFound, refused.Code);
        var stay = await service.GetBookingComplianceAsync(world.Restricted, grantedStay.Id, CancellationToken.None);
        Assert.Equal([alone.Id], stay.Proofs.Select(p => p.Id));
        storage.Opened.Clear();
        await using (var export = await service.ExportAsync(world.Restricted, new ComplianceArchiveExportQuery(DayOf(-3), DayOf(-3)), CancellationToken.None))
        {
            Assert.Equal(1, export.FileCount);
        }

        Assert.DoesNotContain(storage.Opened, key => key.Contains(shared.Id.ToString(), StringComparison.Ordinal));

        var receipts = new AlloggiatiReceiptArchiveSource(db, storage);
        Assert.Equal(new[] { shared.Id, alone.Id }.Order(), (await receipts.Query(world.OrgWide).ToListAsync()).Select(r => r.Id).Order());
        Assert.Equal([alone.Id], (await receipts.Query(world.Restricted).ToListAsync()).Select(r => r.Id));
        Assert.Null(await receipts.OpenAsync(shared.Id, world.Restricted, CancellationToken.None));
        await using var file = (await receipts.OpenAsync(alone.Id, world.Restricted, CancellationToken.None))!.Content;
        Assert.NotNull(file);
    }

    [Fact]
    public async Task ReceiptCoverage_OnlyCoveredFlatsGreen_Test()
    {
        var world = await World.SeedAsync();
        var third = await AddFlatAsync(world, "Villa Verde");
        var (trullo, _) = await world.AddStayAsync(world.Granted, checkInOffset: -2);
        var (casa, _) = await world.AddStayAsync(world.Hidden, checkInOffset: -2);
        var (villa, _) = await world.AddStayAsync(third, checkInOffset: -2);
        var (trulloAnotherDay, _) = await world.AddStayAsync(world.Granted, checkInOffset: -1);
        await world.AddStayAsync(world.Granted, checkInOffset: -2, status: BookingStatus.Cancelled);
        await world.AddStayAsync(world.Hidden, checkInOffset: -2, noShow: true);
        var storage = new RecordingStorage();
        await using var db = world.Db();
        var service = NewService(world, db, storage);

        var summary = await service.GetReceiptCoverageAsync(world.OrgWide, DayOf(-2), [world.Hidden.Id, world.Granted.Id], CancellationToken.None);
        Assert.Equal(2, summary.Stays);
        Assert.Equal([(world.Hidden.Id, 1), (world.Granted.Id, 1)], summary.Properties.Select(flat => (flat.PropertyId, flat.Stays)));
        var thirdSummary = await service.GetReceiptCoverageAsync(world.OrgWide, DayOf(-2), [third.Id], CancellationToken.None);
        Assert.Equal(1, thirdSummary.Stays);

        var before = await world.StatusService(db).EvaluateAsync(world.OrgWide, CancellationToken.None);
        Assert.All(
            new[] { trullo, casa, villa, trulloAnotherDay },
            stay => Assert.Equal(ComplianceLight.Red, ItemOf(before, stay).Verdict.Light));

        var entry = await UploadAsync(service, world.OrgWide, world, world.Granted.Id, DayOf(-2), world.Hidden.Id);
        Assert.Equal(2, entry.CoveredStays);
        Assert.Equal([world.Granted.Id, world.Hidden.Id], entry.CoveredProperties!.Select(flat => flat.Id));

        var after = await world.StatusService(db).EvaluateAsync(world.OrgWide, CancellationToken.None);
        foreach (var proven in new[] { trullo, casa })
        {
            var verdict = ItemOf(after, proven).Verdict;
            Assert.Equal((ComplianceLight.Green, ComplianceEvidenceSources.ManualUpload), (verdict.Light, verdict.EvidenceSource));
            Assert.Equal($"manual-proof:{entry.Id:N}", verdict.ProofReference);
        }

        Assert.Equal(ComplianceLight.Red, ItemOf(after, villa).Verdict.Light);
        Assert.Equal(ComplianceLight.Red, ItemOf(after, trulloAnotherDay).Verdict.Light);

        Assert.NotNull((await world.StatusService(db).GetStayStatusAsync(world.OrgWide, casa.Id, CancellationToken.None))!.ReceiptProof);
        Assert.Null((await world.StatusService(db).GetStayStatusAsync(world.OrgWide, villa.Id, CancellationToken.None))!.ReceiptProof);
        Assert.Null((await world.StatusService(db).GetStayStatusAsync(world.OrgWide, trulloAnotherDay.Id, CancellationToken.None))!.ReceiptProof);

        await world.AddProofAsync(third.Id, ManualProofKind.AlloggiatiReceipt, DayOf(-2));
        var withLegacy = await world.StatusService(db).EvaluateAsync(world.OrgWide, CancellationToken.None);
        Assert.Equal(ComplianceLight.Green, ItemOf(withLegacy, villa).Verdict.Light);
        Assert.Equal(ComplianceLight.Red, ItemOf(withLegacy, trulloAnotherDay).Verdict.Light);
    }

    [Fact]
    public async Task ReceiptCoverage_AStayWhoseCommunicationFailedOrWasPartlyAccepted_IsNotCountedAsTurningGreen_Test()
    {
        var world = await World.SeedAsync();
        world.Senders[world.Granted.Id] = ComplianceSenderType.ChannelManager;
        var (error, _) = await world.AddStayAsync(world.Hidden, checkInOffset: -2, report: AlloggiatiWebStatus.Errore);
        var (rejected, _) = await world.AddStayAsync(world.Hidden, checkInOffset: -2, report: AlloggiatiWebStatus.Rifiutato);
        var (partly, _) = await world.AddStayAsync(world.Hidden, checkInOffset: -2, report: AlloggiatiWebStatus.PartiallyAccepted);
        var (writeFailed, _) = await world.AddStayAsync(world.Granted, checkInOffset: -2, guests: 1);
        await world.EntrustAsync(writeFailed, positions: [0], status: OctorateSyncStatus.Failed);
        var (fineOfTheFlat, _) = await world.AddStayAsync(world.Hidden, checkInOffset: -2);
        var (fineOfTheChannelManager, _) = await world.AddStayAsync(world.Granted, checkInOffset: -2, guests: 1);
        var storage = new RecordingStorage();
        await using var db = world.Db();
        var service = NewService(world, db, storage);

        var summary = await service.GetReceiptCoverageAsync(world.OrgWide, DayOf(-2), [world.Granted.Id, world.Hidden.Id], CancellationToken.None);
        Assert.Equal(2, summary.Stays);
        Assert.Equal([(world.Granted.Id, 1), (world.Hidden.Id, 1)], summary.Properties.Select(flat => (flat.PropertyId, flat.Stays)));

        var entry = await UploadAsync(service, world.OrgWide, world, world.Granted.Id, DayOf(-2), world.Hidden.Id);
        Assert.Equal(2, entry.CoveredStays);

        var after = await world.StatusService(db).EvaluateAsync(world.OrgWide, CancellationToken.None);
        foreach (var green in new[] { fineOfTheFlat, fineOfTheChannelManager })
            Assert.Equal(ComplianceLight.Green, ItemOf(after, green).Verdict.Light);

        foreach (var red in new[] { error, rejected, partly, writeFailed })
            Assert.Equal(ComplianceLight.Red, ItemOf(after, red).Verdict.Light);
    }

    [Fact]
    public async Task ProofRetention_DeletesOnlyExpired_Test()
    {
        var world = await World.SeedAsync();
        var storage = new RecordingStorage();
        var now = world.Clock.GetUtcNow().UtcDateTime;
        var expired = await AddProofAsync(world, world.Org.Id, world.Granted.Id, now.AddYears(-5).AddDays(-1), storage, world.Hidden.Id);
        var expiredOfAnotherOrg = await AddProofAsync(world, world.Foreign.OrgId, world.Foreign.Id, now.AddYears(-6), storage);
        var oneDayLeft = await AddProofAsync(world, world.Org.Id, world.Granted.Id, now.AddYears(-5).AddDays(1), storage, world.Hidden.Id);
        var young = await AddProofAsync(world, world.Org.Id, world.Hidden.Id, now.AddMonths(-3), storage);

        await using (var db = world.Db())
        {
            var job = new ComplianceProofRetentionJob(db, storage, world.Clock, NullLogger<ComplianceProofRetentionJob>.Instance);
            await job.ExecuteAsync(CancellationToken.None);
            await job.ExecuteAsync(CancellationToken.None);
        }

        await using var read = world.Db();
        Assert.Equal(
            new[] { oneDayLeft.Id, young.Id }.Order(),
            (await read.ManualComplianceProofs.AsNoTracking().Select(p => p.Id).ToListAsync()).Order());
        Assert.Equal(
            new[] { oneDayLeft.Id, oneDayLeft.Id, young.Id }.Order(),
            (await read.ManualComplianceProofProperties.AsNoTracking().Select(l => l.ProofId).ToListAsync()).Order());
        Assert.Equal(new[] { expired.FileKey, expiredOfAnotherOrg.FileKey }.Order(StringComparer.Ordinal), storage.Deleted.Order(StringComparer.Ordinal));
        Assert.True(storage.Has(oneDayLeft.FileKey));
        Assert.True(storage.Has(young.FileKey));
        Assert.False(storage.Has(expired.FileKey));
    }

    [Fact]
    public async Task ProofRetention_AFileThatCannotBeDeleted_KeepsItsRowAndFailsTheRun_Test()
    {
        var world = await World.SeedAsync();
        var storage = new RecordingStorage();
        var now = world.Clock.GetUtcNow().UtcDateTime;
        var stuck = await AddProofAsync(world, world.Org.Id, world.Granted.Id, now.AddYears(-5).AddDays(-2), storage);
        var fine = await AddProofAsync(world, world.Org.Id, world.Hidden.Id, now.AddYears(-5).AddDays(-3), storage);
        storage.FailOnDelete.Add(stuck.FileKey);

        await using (var db = world.Db())
        {
            var failure = await Assert.ThrowsAsync<InvalidOperationException>(
                () => new ComplianceProofRetentionJob(db, storage, world.Clock, NullLogger<ComplianceProofRetentionJob>.Instance).ExecuteAsync(CancellationToken.None));
            Assert.Contains("1 compliance proof", failure.Message, StringComparison.Ordinal);
        }

        await using (var read = world.Db())
            Assert.Equal([stuck.Id], await read.ManualComplianceProofs.AsNoTracking().Select(p => p.Id).ToListAsync());

        storage.FailOnDelete.Clear();
        await using (var db = world.Db())
            await new ComplianceProofRetentionJob(db, storage, world.Clock, NullLogger<ComplianceProofRetentionJob>.Instance).ExecuteAsync(CancellationToken.None);

        await using var after = world.Db();
        Assert.Empty(await after.ManualComplianceProofs.AsNoTracking().ToListAsync());
        Assert.Empty(await after.ManualComplianceProofProperties.AsNoTracking().ToListAsync());
    }

    private static ComplianceArchiveService NewService(World world, AppDbContext db, IFileStorage storage)
    {
        var flags = new Mock<IFeatureFlags>();
        flags.Setup(f => f.IsEnabled(FeatureFlags.ComplianceTrafficLight)).Returns(true);
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

    private static async Task<ComplianceArchiveEntry> UploadAsync(
        ComplianceArchiveService service,
        HostScope scope,
        World world,
        Guid propertyId,
        DateOnly day,
        params Guid[] others) =>
        await UploadAsync(service, scope, world, propertyId, day, others, ManualProofKind.AlloggiatiReceipt);

    private static async Task<ComplianceArchiveEntry> UploadAsync(
        ComplianceArchiveService service,
        HostScope scope,
        World world,
        Guid propertyId,
        DateOnly day,
        Guid[] others,
        ManualProofKind kind)
    {
        await using var content = new MemoryStream(PdfBytes);
        return await service.UploadProofAsync(
            scope,
            new UploadComplianceProofRequest(propertyId, kind, day, world.Owner.Id, others),
            content,
            CancellationToken.None);
    }

    /// <summary>A flat of the org of the world, in Italy unless a country is given.</summary>
    private static async Task<Property> AddFlatAsync(World world, string name, string country = "IT")
    {
        var flat = HostScopeScenario.NewProperty(world.Org.Id, world.Owner.Id, name);
        flat.Country = country;
        await using var db = world.Db();
        db.Properties.Add(flat);
        await db.SaveChangesAsync();
        return flat;
    }

    private static ComplianceStatusItem ItemOf(ComplianceStatusResult status, Booking stay) =>
        Assert.Single(status.Items, i => i.Kind == ComplianceItemKind.Alloggiati && i.BookingId == stay.Id);

    private static async Task<ManualComplianceProof> AddProofAsync(
        World world,
        Guid orgId,
        Guid propertyId,
        DateTime uploadedAt,
        RecordingStorage storage,
        params Guid[] alsoCovering)
    {
        await using var db = world.Db();
        var id = Guid.NewGuid();
        var key = StorageKeys.ManualComplianceProof(orgId, id, $"{Guid.NewGuid():N}.pdf");
        storage.Put(key);
        var proof = new ManualComplianceProof
        {
            Id = id,
            OrgId = orgId,
            PropertyId = propertyId,
            Kind = ManualProofKind.AlloggiatiReceipt,
            Day = DayOf(-3),
            FileKey = key,
            ContentType = "application/pdf",
            UploadedByUserId = world.Owner.Id,
            UploadedAt = uploadedAt,
        };
        db.ManualComplianceProofs.Add(proof);
        foreach (var flat in new[] { propertyId }.Concat(alsoCovering))
            db.ManualComplianceProofProperties.Add(new ManualComplianceProofProperty { OrgId = orgId, ProofId = id, PropertyId = flat });

        await db.SaveChangesAsync();
        return proof;
    }

    private sealed class RecordingStorage : IFileStorage
    {
        private readonly Dictionary<string, byte[]> _files = [];

        public List<string> Puts { get; } = [];

        public List<string> Opened { get; } = [];

        public List<string> Deleted { get; } = [];

        public HashSet<string> FailOnDelete { get; } = [];

        public void Put(string key) => _files[key] = PdfBytes;

        public bool Has(string key) => _files.ContainsKey(key);

        public async Task PutAsync(StorageBucket bucket, string key, Stream content, string contentType, CancellationToken cancellationToken = default)
        {
            using var copy = new MemoryStream();
            await content.CopyToAsync(copy, cancellationToken);
            _files[key] = copy.ToArray();
            Puts.Add(key);
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

        public string GetPublicUrl(string key) => throw new InvalidOperationException("A compliance proof has no public address.");

        public string? TryGetPublicKey(string url) => null;

        public Task<Uri?> GetSignedReadUrlAsync(string key, TimeSpan lifetime, string? downloadFileName, CancellationToken cancellationToken = default) =>
            Task.FromResult<Uri?>(null);
    }
}
