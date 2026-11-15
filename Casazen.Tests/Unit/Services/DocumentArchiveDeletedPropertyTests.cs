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
        var dedicated = new ComplianceArchiveService(
            world.Db,
            world.Storage,
            Mock.Of<IComplianceStatusService>(),
            new MigraDocPdfDocumentRenderer(),
            Mock.Of<IFeatureFlags>(),
            Options.Create(new ComplianceOptions()),
            NullLogger<ComplianceArchiveService>.Instance);
        var history = await dedicated.ListAsync(wholeOrg, new ComplianceArchiveQuery(), CancellationToken.None);
        Assert.Contains(history.Items, i => i.Id == hiddenReceipt);
        Assert.Contains(history.Items, i => i.Id == hiddenIstat);
    }
}
