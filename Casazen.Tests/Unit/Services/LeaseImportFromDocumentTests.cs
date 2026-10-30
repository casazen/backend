using System.Text;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Features;
using Casazen.Core.Services;
using Casazen.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// NL-DOCS: the owner uploaded the signed contract to be read; the final import takes <b>that</b> document and does not ask for the file again.
/// And the one rule that costs if it fails: the document has to be a contract document of <b>this</b> lease (the same draft, so the same org),
/// never another's.
/// </summary>
public sealed class LeaseImportFromDocumentTests
{
    private static readonly byte[] Pdf = Encoding.ASCII.GetBytes("%PDF-1.4\n% the signed contract\n%%EOF\n");

    [Fact]
    public async Task TheContractUploadedToTheDraft_IsImportedWithoutSendingItAgain_AndStaysTheOnlyContractDocument()
    {
        using var harness = await new LeaseDraftHarness().SeedAsync();
        var draft = await ImportedDraftAsync(harness);
        var document = await AddContractDocumentAsync(harness, draft);
        ServePdf(harness);

        var signed = await CreateSigning(harness).ImportSignedContractFromDocumentAsync(
            draft, "auth0|owner", new ImportedDocumentDeclaration(new DateTime(2026, 9, 30, 0, 0, 0, DateTimeKind.Utc), document.Id));

        Assert.Equal(LeaseStatus.Signed, signed.Status);
        Assert.NotNull(signed.SignedPdfStoragePath);
        Assert.NotEqual(document.StorageKey, signed.SignedPdfStoragePath); // two owners, two keys: deleting the one never takes the other
        await using var db = harness.SecondContext();
        var documents = await db.LeaseDocuments.AsNoTracking().Where(d => d.LeaseContractId == draft).ToListAsync();
        Assert.Equal(document.Id, Assert.Single(documents).Id); // the document the owner uploaded, not a second copy
        harness.Storage.Verify(
            s => s.PutAsync(StorageBucket.Private, It.IsAny<string>(), It.IsAny<Stream>(), "application/pdf", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ADocumentThatIsNotAContractDocumentOfThisLease_IsNotThere_AndNothingIsWritten()
    {
        using var harness = await new LeaseDraftHarness().SeedAsync();
        var draft = await ImportedDraftAsync(harness);
        var other = await ImportedDraftAsync(harness);
        var ofAnotherLease = await AddContractDocumentAsync(harness, other);
        var attestation = await AddContractDocumentAsync(harness, draft, LeaseDocumentKind.Attestation);
        var ofAnotherOrg = await AddContractDocumentAsync(harness, draft, orgId: Guid.NewGuid());
        ServePdf(harness);
        var signing = CreateSigning(harness);
        var day = new DateTime(2026, 9, 30, 0, 0, 0, DateTimeKind.Utc);

        // DATA RULE PROOF (tenancy and permissions): neither the contract of another lease, nor another kind of document, nor a document
        // that says it belongs to another org can be the signed contract of this lease.
        foreach (var id in new[] { ofAnotherLease.Id, attestation.Id, ofAnotherOrg.Id, Guid.NewGuid() })
        {
            var refused = await Assert.ThrowsAsync<NotFoundException>(
                () => signing.ImportSignedContractFromDocumentAsync(draft, "auth0|owner", new ImportedDocumentDeclaration(day, id)));
            Assert.Equal("document_not_found", refused.Code);
        }

        harness.Storage.Verify(
            s => s.PutAsync(It.IsAny<StorageBucket>(), It.IsAny<string>(), It.IsAny<Stream>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        var untouched = await harness.LoadAsync(draft);
        Assert.Equal(LeaseStatus.Draft, untouched.Status);
        Assert.Null(untouched.SignedPdfStoragePath);
    }

    /// <summary>
    /// DATA RULE PROOF (CF-IMPORT): the draft of «Parto da zero» is an <b>imported</b> concordato marked for the partner, whose dates and rent
    /// are missing (not values); once filled with what the contract CF delivers states, the contract uploaded to it imports: it is
    /// <c>Signed</c> and still marked as the partner's.
    /// </summary>
    [Fact]
    public async Task AConcordatoFacileDraft_IsImportedWithTheContractTheyDeliver_AndStaysMarkedAsTheirs()
    {
        using var harness = await new LeaseDraftHarness().SeedAsync();

        var draft = await harness.Service.CreateCfDraftAsync(harness.PropertyId, harness.OrgId);

        Assert.Equal(draft, await harness.Service.CreateCfDraftAsync(harness.PropertyId, harness.OrgId)); // one draft per property
        var started = await harness.Service.GetAsync(draft);
        Assert.Equal(LeaseOrigin.Imported, started.Origin);
        Assert.Equal(nameof(LeaseContractType.Concordato), started.Values.ContractType);
        Assert.Null(started.Values.StartDate);
        Assert.Null(started.Values.MonthlyRent);
        Assert.NotEmpty(started.Flow[2].Needs); // the parties are asked
        Assert.NotEmpty(started.Flow[4].Needs); // and the start of the contract: the starting date of the column is no date

        var version = (await harness.SaveAsync(draft, started.Version, 2, """
            {"landlords":[{"firstName":"Mario","lastName":"Rossi","fiscalCode":"RSSMRA80A01H501U","citizenship":"IT","residence":"Bari, via Sparano 1"}],
             "tenants":[{"firstName":"Giulia","lastName":"Verdi","fiscalCode":"VRDGLI85B02F205A","citizenship":"IT","residence":"Bari, via Argiro 7"}]}
            """)).Version;
        version = (await harness.SaveAsync(draft, version, 3, """{"contractType":"Concordato","taxRegime":"CedolareSecca","monthlyRent":700}""")).Version;
        await harness.SaveAsync(draft, version, 4, """{"startDate":"2026-01-15"}""");
        var document = await AddContractDocumentAsync(harness, draft);
        ServePdf(harness);

        var signed = await CreateSigning(harness).ImportSignedContractFromDocumentAsync(
            draft, "auth0|owner", new ImportedDocumentDeclaration(new DateTime(2026, 1, 10, 0, 0, 0, DateTimeKind.Utc), document.Id));

        Assert.Equal(LeaseStatus.Signed, signed.Status);
        Assert.True(signed.IsConcordatoFacileSource);
        Assert.Equal(LeaseContractType.Concordato, signed.ContractType);
        Assert.Equal(700m, signed.MonthlyRent);
    }

    // ---- helpers ---------------------------------------------------------------------------------------------------------

    /// <summary>An imported draft with everything the first four steps ask, a contract that started in January (already in force).</summary>
    private static async Task<Guid> ImportedDraftAsync(LeaseDraftHarness harness)
    {
        var created = await harness.CreateAsync(LeaseOrigin.Imported);
        var version = (await harness.SaveAsync(created.Id, created.Version, 2, """
            {"landlords":[{"firstName":"Mario","lastName":"Rossi","fiscalCode":"RSSMRA80A01H501U","citizenship":"IT","residence":"Bari, via Sparano 1"}],
             "tenants":[{"firstName":"Giulia","lastName":"Verdi","fiscalCode":"VRDGLI85B02F205A","citizenship":"IT","residence":"Bari, via Argiro 7"}]}
            """)).Version;
        version = (await harness.SaveAsync(created.Id, version, 3, """{"contractType":"Libero","taxRegime":"CedolareSecca","monthlyRent":900}""")).Version;
        await harness.SaveAsync(created.Id, version, 4, """{"startDate":"2026-01-15"}""");
        return created.Id;
    }

    private static async Task<LeaseDocument> AddContractDocumentAsync(
        LeaseDraftHarness harness, Guid leaseId, LeaseDocumentKind kind = LeaseDocumentKind.Contract, Guid? orgId = null)
    {
        var document = new LeaseDocument
        {
            LeaseContractId = leaseId,
            OrgId = orgId ?? harness.OrgId,
            Kind = kind,
            FileName = "contratto.pdf",
            StorageKey = $"orgs/{harness.OrgId}/leases/{leaseId}/documents/{Guid.NewGuid():N}.pdf",
            UploadedBy = "auth0|owner",
            SizeBytes = Pdf.Length,
            ExtractionStatus = LeaseDocumentExtractionStatus.Completed,
        };
        harness.Db.LeaseDocuments.Add(document);
        await harness.Db.SaveChangesAsync();
        harness.Db.ChangeTracker.Clear();
        return document;
    }

    private static void ServePdf(LeaseDraftHarness harness) =>
        harness.Storage
            .Setup(s => s.OpenReadAsync(StorageBucket.Private, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new MemoryStream(Pdf));

    private static LeaseSigningService CreateSigning(LeaseDraftHarness harness)
    {
        var provider = new Mock<ILeaseESignService>();
        provider.SetupGet(p => p.IsConfigured).Returns(false);
        return new LeaseSigningService(
            harness.Db,
            provider.Object,
            Mock.Of<IFeatureFlags>(),
            Mock.Of<ILeaseTemplateService>(),
            harness.Storage.Object,
            Mock.Of<IApeComplianceService>(),
            NullLogger<LeaseSigningService>.Instance,
            harness.Clock);
    }
}
