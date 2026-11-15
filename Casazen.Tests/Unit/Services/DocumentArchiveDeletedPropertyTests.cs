using Casazen.Core.Entities;
using Casazen.Core.Exceptions;
using Casazen.Core.Features;
using Casazen.Core.Options;
using Casazen.Core.Services;
using Casazen.Infrastructure.Documents;
using Casazen.Infrastructure.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// E20/F02/US01/T01 (PC-05, NF-99): the Alloggiati receipts and the ISTAT proofs are the fourth and the fifth source of the document archive, and the list, the count
/// and the export concatenate ALL the sources in one statement. EF applies a lifted filter (<c>IgnoreQueryFilters</c>) to the whole statement, so a source that lifted the
/// soft-delete filter to keep the proofs of a deleted flat would also bring back the documents and the lease files of a deleted property. The tests that ask for the three
/// types of the documents only never put the proof sources in the statement: this is the one that runs the container's five sources together.
/// </summary>
public class DocumentArchiveDeletedPropertyTests
{
    private static DocumentArchiveQuery All { get; } = new(null, null, null, null, null, null, 1, DocumentArchiveQuery.MaxPageSize);

    /// <summary>
    /// PROOF TEST: data rule. A property deleted since (PC-05) leaves the archive: its documents, the files of its lease and its proofs are not in the list, in the
    /// count by type, in the ZIP, and cannot be opened, whatever the sources that share the statement; what belongs to the other property is all there. The history of
    /// the proofs of the deleted flat is the dedicated archive's, which reads them with its own statement.
    /// </summary>
    [Fact]
    public async Task DocumentArchive_TheDocumentsOfADeletedProperty_LeaveTheListTheCountTheZipAndTheDownload_WithTheFiveSourcesTogether_Test()
    {
        var world = await DocumentArchiveWorld.CreateAsync();
        var day = new DateOnly(2026, 10, 3);
        var grantedReceipt = await world.AddProofAsync(world.Hosts.Granted, ManualProofKind.AlloggiatiReceipt, day);
        var hiddenReceipt = await world.AddProofAsync(world.Hosts.Hidden, ManualProofKind.AlloggiatiReceipt, day);
        var grantedIstat = await world.AddProofAsync(world.Hosts.Granted, ManualProofKind.IstatProof, new DateOnly(2026, 9, 1));
        var hiddenIstat = await world.AddProofAsync(world.Hosts.Hidden, ManualProofKind.IstatProof, new DateOnly(2026, 9, 1));
        var service = world.Service();
        var wholeOrg = world.Hosts.OrgWide;

        // Before: everything of the org in the one statement of the five sources (two property documents, two lease files, one tourist tax receipt, four proofs).
        Assert.Equal(9, (await service.ListAsync(wholeOrg, DocumentArchiveWorld.EveryType, All)).TotalCount);

        await world.SoftDeleteAsync(world.Hosts.Hidden);

        var list = await service.ListAsync(wholeOrg, DocumentArchiveWorld.EveryType, All);
        Assert.Equal(
            new[] { world.GrantedPropertyDocument, world.GrantedLeaseDocument, world.TaxReceipt, grantedReceipt, grantedIstat }.Order(),
            list.Items.Select(i => i.Id).Order());
        Assert.Equal(5, list.TotalCount);
        Guid[] gone = [world.HiddenPropertyDocument, world.HiddenLeaseDocument, hiddenReceipt, hiddenIstat];
        Assert.DoesNotContain(list.Items, i => gone.Contains(i.Id));

        var counts = await service.CountByTypeAsync(wholeOrg, DocumentArchiveWorld.EveryType, All);
        Assert.Equal(
            [
                (ArchiveDocumentType.AlloggiatiReceipt, 1),
                (ArchiveDocumentType.IstatProof, 1),
                (ArchiveDocumentType.TouristTax, 1),
                (ArchiveDocumentType.Contract, 1),
                (ArchiveDocumentType.PropertyDocument, 1),
            ],
            counts.Select(c => (c.Type, c.Count)));

        using var destination = new MemoryStream();
        var exported = await service.ExportAsync(wholeOrg, DocumentArchiveWorld.EveryType, All, destination);
        Assert.Equal((5, 5), (exported.Documents, exported.Files));

        // What the list does not show cannot be opened, and what it shows can.
        foreach (var (type, id) in new[]
                 {
                     (ArchiveDocumentType.PropertyDocument, world.HiddenPropertyDocument),
                     (ArchiveDocumentType.Contract, world.HiddenLeaseDocument),
                     (ArchiveDocumentType.AlloggiatiReceipt, hiddenReceipt),
                     (ArchiveDocumentType.IstatProof, hiddenIstat),
                 })
        {
            await Assert.ThrowsAsync<NotFoundException>(() => service.OpenAsync(wholeOrg, DocumentArchiveWorld.EveryType, type, id));
        }

        var file = await service.OpenAsync(wholeOrg, DocumentArchiveWorld.EveryType, ArchiveDocumentType.AlloggiatiReceipt, grantedReceipt);
        await using (file.Content)
        {
            Assert.Equal("application/pdf", file.ContentType);
        }

        // The history of the proofs of the deleted flat: the dedicated archive keeps it (its own statement lifts the filter, shared with no other source).
        var history = await DedicatedArchive(world).ListAsync(wholeOrg, new ComplianceArchiveQuery(), CancellationToken.None);
        Assert.Contains(history.Items, i => i.Id == hiddenReceipt);
        Assert.Contains(history.Items, i => i.Id == hiddenIstat);
    }

    /// <summary>
    /// PROOF TEST: permission rule. A collaborator «Solo alcuni» reads a receipt of several flats only when EVERY flat it covers is in its reach, a flat deleted since
    /// included: the soft-delete filter of the statement takes a deleted flat out of the check of the reach, so the lines of the receipt are counted against the lines whose
    /// flat is still there and a receipt that covers a flat gone stays out. With a receipt of two flats, one of them deleted afterwards, and a collaborator who reaches only the
    /// other: it does not read it in the list, the count, the ZIP or the download of the general archive (the answer of the dedicated archive too), a receipt of its own flat
    /// alone it still reads, and whoever has the whole reach reads both.
    /// </summary>
    [Fact]
    public async Task DocumentArchive_AReceiptThatCoversAFlatDeletedSince_IsNotReadByACollaboratorWhoDoesNotReachIt_Test()
    {
        var world = await DocumentArchiveWorld.CreateAsync();
        var shared = await world.AddProofAsync(world.Hosts.Granted, ManualProofKind.AlloggiatiReceipt, new DateOnly(2026, 10, 3), world.Hosts.Hidden);
        var sharedIstat = await world.AddProofAsync(world.Hosts.Granted, ManualProofKind.IstatProof, new DateOnly(2026, 9, 1), world.Hosts.Hidden);
        var alone = await world.AddProofAsync(world.Hosts.Granted, ManualProofKind.AlloggiatiReceipt, new DateOnly(2026, 10, 4));
        var service = world.Service();
        var collaborator = world.Hosts.Restricted;
        var wholeOrg = world.Hosts.OrgWide;

        // Before the deletion the flat that is not the collaborator's is out of its reach: the receipts that cover it are not read.
        var before = await service.ListAsync(collaborator, DocumentArchiveWorld.EveryType, All);
        Assert.Equal(
            new[] { world.GrantedPropertyDocument, world.GrantedLeaseDocument, alone }.Order(),
            before.Items.Select(i => i.Id).Order());

        await world.SoftDeleteAsync(world.Hosts.Hidden);

        // After it the answer is the same, whatever the flat that is gone: in the list, the count, the ZIP and the download.
        var list = await service.ListAsync(collaborator, DocumentArchiveWorld.EveryType, All);
        Assert.Equal(
            new[] { world.GrantedPropertyDocument, world.GrantedLeaseDocument, alone }.Order(),
            list.Items.Select(i => i.Id).Order());
        Assert.Equal(3, list.TotalCount);
        var counts = await service.CountByTypeAsync(collaborator, DocumentArchiveWorld.EveryType, All);
        Assert.Equal(
            [
                (ArchiveDocumentType.AlloggiatiReceipt, 1),
                (ArchiveDocumentType.IstatProof, 0),
                (ArchiveDocumentType.TouristTax, 0),
                (ArchiveDocumentType.Contract, 1),
                (ArchiveDocumentType.PropertyDocument, 1),
            ],
            counts.Select(c => (c.Type, c.Count)));
        using var zip = new MemoryStream();
        var exported = await service.ExportAsync(collaborator, DocumentArchiveWorld.EveryType, All, zip);
        Assert.Equal((3, 3), (exported.Documents, exported.Files));
        await Assert.ThrowsAsync<NotFoundException>(
            () => service.OpenAsync(collaborator, DocumentArchiveWorld.EveryType, ArchiveDocumentType.AlloggiatiReceipt, shared));
        await Assert.ThrowsAsync<NotFoundException>(
            () => service.OpenAsync(collaborator, DocumentArchiveWorld.EveryType, ArchiveDocumentType.IstatProof, sharedIstat));
        var own = await service.OpenAsync(collaborator, DocumentArchiveWorld.EveryType, ArchiveDocumentType.AlloggiatiReceipt, alone);
        await using (own.Content)
        {
            Assert.Equal("application/pdf", own.ContentType);
        }

        // The dedicated archive gives the same answer for the collaborator; whoever has the whole reach reads the receipts of both flats in either archive.
        var dedicated = DedicatedArchive(world);
        var dedicatedOfTheCollaborator = await dedicated.ListAsync(collaborator, new ComplianceArchiveQuery(), CancellationToken.None);
        Assert.Equal([alone], dedicatedOfTheCollaborator.Items.Select(i => i.Id));
        var generalOfTheOrg = await service.ListAsync(wholeOrg, DocumentArchiveWorld.EveryType, All);
        Assert.Contains(generalOfTheOrg.Items, i => i.Id == shared);
        Assert.Contains(generalOfTheOrg.Items, i => i.Id == sharedIstat);
        var shownToTheOrg = await service.OpenAsync(wholeOrg, DocumentArchiveWorld.EveryType, ArchiveDocumentType.AlloggiatiReceipt, shared);
        await using (shownToTheOrg.Content)
        {
            Assert.Equal("application/pdf", shownToTheOrg.ContentType);
        }

        var dedicatedOfTheOrg = await dedicated.ListAsync(wholeOrg, new ComplianceArchiveQuery(), CancellationToken.None);
        Assert.Equal(new[] { shared, sharedIstat, alone }.Order(), dedicatedOfTheOrg.Items.Select(i => i.Id).Order());
    }

    /// <summary>The dedicated archive of the proofs (<c>api/compliance/archive</c>): its own statement, which lifts the soft-delete filter to keep the history.</summary>
    private static ComplianceArchiveService DedicatedArchive(DocumentArchiveWorld world) =>
        new(
            world.Db,
            world.Storage,
            Mock.Of<IComplianceStatusService>(),
            new MigraDocPdfDocumentRenderer(),
            Mock.Of<IFeatureFlags>(),
            Options.Create(new ComplianceOptions()),
            NullLogger<ComplianceArchiveService>.Instance);
}
