using System.Text;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Leases.Reading;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Services;
using Casazen.Tests.Unit.Leases;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// CF-DOCS-REAL, the rules of the reading of the document the guided service delivers, on the EF InMemory database:
/// <list type="bullet">
/// <item>a contract that comes with its attestation and its calculation sheet is <b>read on its own pages</b> and the draft is filled from them, while
/// what is kept with the document is the sections and the codes: <b>no value</b> of a person, no amount, no town;</item>
/// <item>the receipt of the registration is read as a <b>digital</b> PDF, with no OCR; a request the Agenzia discarded is <b>not a registration</b>;
/// what is kept holds no value, and the values go to the person only when they ask, from the PDF, <b>from a document of this lease and no other</b>.</item>
/// </list>
/// </summary>
public sealed class CfDocsRealExtractionTests
{
    [Fact]
    public async Task AContractWithItsAttestationAndSheet_IsReadOnItsOwnPages_AndTheResultKeepsOnlySectionsAndCodes()
    {
        using var harness = await new LeaseDraftHarness().SeedAsync();

        // The house of the draft is the one the contract names: a contract about another address would be a conflict to look at, not the case here.
        var property = await harness.Db.Properties.SingleAsync(p => p.Id == harness.PropertyId);
        property.Address = "Via Verdi 12";
        property.City = "Bollate";
        await harness.Db.SaveChangesAsync();
        harness.Db.ChangeTracker.Clear();

        var draft = await harness.CreateAsync(LeaseOrigin.Imported);
        var document = await AddDocumentAsync(harness.Db, harness.OrgId, draft.Id, LeaseDocumentKind.Contract);
        harness.Storage
            .Setup(s => s.OpenReadAsync(StorageBucket.Private, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new MemoryStream(Encoding.ASCII.GetBytes("%PDF-1.4 a scan")));
        var reader = new Mock<IContractTextReader>();
        reader.Setup(r => r.ReadAsync(It.IsAny<byte[]>(), It.IsAny<CancellationToken>())).ReturnsAsync(new ContractText(RealFormatDocument.Text(), 14, 14, false));

        await new LeaseDocumentExtractionService(harness.Db, harness.Storage.Object, reader.Object, harness.Service, NullLogger<LeaseDocumentExtractionService>.Instance)
            .ExtractAsync(document.Id);

        // The draft has what the CONTRACT says: the rent and the deposit of its articles, not the rents and the amounts of the attestation and the sheet.
        var read = await harness.Service.GetAsync(draft.Id);
        Assert.Equal(595m, read.Values.MonthlyRent);
        Assert.Equal(1785m, read.Values.SecurityDeposit);
        Assert.Equal("2026-09-19", read.Values.StartDate);
        Assert.Equal(RealFormatDocument.LandlordFiscalCode, Assert.Single(read.Values.Landlords).FiscalCode);
        Assert.Equal(RealFormatDocument.TenantFiscalCode, Assert.Single(read.Values.Tenants).FiscalCode);

        await using var second = harness.SecondContext();
        var stored = await second.LeaseDocuments.AsNoTracking().SingleAsync(d => d.Id == document.Id);
        Assert.Equal(LeaseDocumentExtractionStatus.Completed, stored.ExtractionStatus);
        Assert.Contains("\"containsAttestation\":true", stored.ExtractionResult);
        Assert.Contains("\"containsCalculationSheet\":true", stored.ExtractionResult);
        Assert.Contains("\"kind\":\"attestation\",\"firstPage\":7,\"lastPage\":10", stored.ExtractionResult);

        // DATA RULE PROOF (privacy): the attestation and the sheet name the people, the town, the organizations and the rent: none of it is kept.
        foreach (var personal in new[]
                 {
                     "Mario", "Rossi", "Giulia", "Verdi", RealFormatDocument.LandlordFiscalCode, RealFormatDocument.TenantFiscalCode, "Bollate",
                     "Garbagnate", "FEDERCASA", "Confproprieta", "595", "7.140", "1785", "Via Verdi",
                 })
        {
            Assert.DoesNotContain(personal, stored.ExtractionResult);
        }

        // The screen of the documents is told the sections, in words of its own, from the list of the documents and from the reading of the draft.
        var documents = new LeaseDocumentService(harness.Db, harness.Storage.Object, NullLogger<LeaseDocumentService>.Instance);
        var listed = Assert.Single(await documents.ListAsync(draft.Id));
        Assert.True(listed.ContainsAttestation);
        Assert.True(listed.ContainsCalculationSheet);
        Assert.Equal(["contract", "attestation", "calculationSheet"], listed.Sections!.Select(s => s.Kind));
        var reading = await documents.GetReadingAsync(draft.Id);
        Assert.Equal((7, 10), (reading.Documents[0].Sections![1].FirstPage, reading.Documents[0].Sections![1].LastPage));
        Assert.Empty(reading.Documents[0].Warnings!);
    }

    [Fact]
    public async Task TheReceiptOfTheRegistration_IsReadWithNoOcr_AndTheResultKeepsNoValue_AndADiscardedRequestIsNotARegistration()
    {
        var (db, lease) = await SeedSignedLeaseAsync();
        var registered = await AddDocumentAsync(db, lease.OrgId, lease.Id, LeaseDocumentKind.Registration);
        var discarded = await AddDocumentAsync(db, lease.OrgId, lease.Id, LeaseDocumentKind.Registration);
        var scan = await AddDocumentAsync(db, lease.OrgId, lease.Id, LeaseDocumentKind.Registration);
        var reader = new Mock<IContractTextReader>();
        var texts = new Queue<string?>([RealFormatDocument.Receipt(), RealFormatDocument.Receipt(discarded: true), null]);
        reader.Setup(r => r.ReadDigitalAsync(It.IsAny<byte[]>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => texts.Dequeue() is { } text ? new ContractText(text, 3, 0, false) : null);
        var sut = new LeaseDocumentExtractionService(db, StorageOf().Object, reader.Object, Mock.Of<ILeaseDraftService>(), NullLogger<LeaseDocumentExtractionService>.Instance);

        await sut.ExtractAsync(registered.Id);
        await sut.ExtractAsync(discarded.Id);
        await sut.ExtractAsync(scan.Id);

        var first = await db.LeaseDocuments.AsNoTracking().SingleAsync(d => d.Id == registered.Id);
        Assert.Equal(LeaseDocumentExtractionStatus.Completed, first.ExtractionStatus);
        Assert.Contains("\"outcome\":\"Registered\"", first.ExtractionResult);

        // DATA RULE PROOF (privacy): the code, the number, the date, the rent and the town of the receipt are not kept with the document.
        foreach (var value in new[] { "TKM26T012345000AB", "012345", "7140", "7.140", "BOLLATE", "MILANO", "2026-09-21", "21/09/2026" })
            Assert.DoesNotContain(value, first.ExtractionResult);

        // DATA RULE PROOF: what the Agenzia discarded is said as such and needs the person's eyes; it is never read as a registration.
        var second = await db.LeaseDocuments.AsNoTracking().SingleAsync(d => d.Id == discarded.Id);
        Assert.Equal(LeaseDocumentExtractionStatus.NeedsReview, second.ExtractionStatus);
        Assert.Contains("\"outcome\":\"Rejected\"", second.ExtractionResult);

        // A scan or a photo of the receipt is not read: the person types the number and the date as before.
        var third = await db.LeaseDocuments.AsNoTracking().SingleAsync(d => d.Id == scan.Id);
        Assert.Equal(LeaseDocumentExtractionStatus.Failed, third.ExtractionStatus);
        Assert.Equal("no_text", third.ExtractionError);
        reader.Verify(r => r.ReadAsync(It.IsAny<byte[]>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task TheValuesOfTheReceipt_AreGivenFromTheFile_ForADocumentOfThisLeaseOnly()
    {
        var (db, lease) = await SeedSignedLeaseAsync();
        var receipt = await AddDocumentAsync(db, lease.OrgId, lease.Id, LeaseDocumentKind.Registration);
        var contract = await AddDocumentAsync(db, lease.OrgId, lease.Id, LeaseDocumentKind.Contract);
        var reader = new Mock<IContractTextReader>();
        reader.Setup(r => r.ReadDigitalAsync(It.IsAny<byte[]>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ContractText(RealFormatDocument.Receipt(), 3, 0, false));
        var sut = new LeaseDocumentReadingService(db, StorageOf().Object, reader.Object);

        var reading = await sut.ReadAsync(lease.Id, receipt.Id);

        Assert.Equal(LeaseDocumentReadingStates.Read, reading!.State);
        Assert.Equal("Registered", reading.Receipt!.Outcome);
        Assert.Equal("TKM26T012345000AB", reading.Receipt.Code);
        Assert.Equal("2026-09-21", reading.Receipt.RegistrationDate);
        Assert.Equal(7140m, reading.Receipt.AnnualRent);
        Assert.Empty(reading.Receipt.Warnings);

        // DATA RULE PROOF (tenancy): the document of another lease, of another kind or that does not exist is the same nothing, and the file is not opened.
        var storage = StorageOf();
        var guarded = new LeaseDocumentReadingService(db, storage.Object, reader.Object);
        Assert.Null(await guarded.ReadAsync(Guid.NewGuid(), receipt.Id));
        Assert.Null(await guarded.ReadAsync(lease.Id, contract.Id));
        Assert.Null(await guarded.ReadAsync(lease.Id, Guid.NewGuid()));
        storage.Verify(s => s.OpenReadAsync(It.IsAny<StorageBucket>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ---- helpers ---------------------------------------------------------------------------------------------------------

    private static Mock<IFileStorage> StorageOf()
    {
        var storage = new Mock<IFileStorage>();
        storage
            .Setup(s => s.OpenReadAsync(StorageBucket.Private, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new MemoryStream(Encoding.ASCII.GetBytes("%PDF-1.4 a document")));
        return storage;
    }

    private static async Task<LeaseDocument> AddDocumentAsync(AppDbContext db, Guid orgId, Guid leaseId, LeaseDocumentKind kind)
    {
        var document = new LeaseDocument
        {
            LeaseContractId = leaseId,
            OrgId = orgId,
            Kind = kind,
            FileName = "documento.pdf",
            StorageKey = $"orgs/{orgId}/leases/{leaseId}/documents/{Guid.NewGuid():N}.pdf",
            UploadedBy = "auth0|owner",
            SizeBytes = 28,
            ExtractionStatus = LeaseDocumentExtractionStatus.Processing,
        };
        db.LeaseDocuments.Add(document);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return document;
    }

    /// <summary>A lease signed and ready to be registered, with the figures of <see cref="RealFormatDocument"/>.</summary>
    private static async Task<(AppDbContext Db, LeaseContract Lease)> SeedSignedLeaseAsync()
    {
        var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase($"CfDocsReal_{Guid.NewGuid()}").Options);
        var org = new Casazen.Core.Entities.Org { Id = Guid.NewGuid(), Name = "Test Org" };
        var property = new Property { Id = Guid.NewGuid(), OrgId = org.Id, OwnerId = "test-user-id", Name = "Test Property", City = "Bollate" };
        var lease = new LeaseContract
        {
            Id = Guid.NewGuid(),
            OrgId = org.Id,
            PropertyId = property.Id,
            Property = property,
            Status = LeaseStatus.Signed,
            ContractType = LeaseContractType.Concordato,
            TaxRegime = LeaseTaxRegime.CedolareSecca,
            FiscalRegime = FiscalRegime.CedolareSecca,
            StartDate = new DateTime(2026, 9, 19),
            EndDate = new DateTime(2029, 9, 18),
            StipulaDate = new DateTime(2026, 9, 19),
            MonthlyRent = 595m,
        };
        db.Orgs.Add(org);
        db.Properties.Add(property);
        db.LeaseContracts.Add(lease);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return (db, lease);
    }
}
