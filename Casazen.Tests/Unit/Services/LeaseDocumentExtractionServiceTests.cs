using System.Text;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
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
/// Proof tests for the rules of the reading of a lease document (ADR-009, NL-DOCS), on the EF InMemory database:
/// <list type="bullet">
/// <item>the contract of an imported draft fills what is empty and <b>never overwrites what the person typed</b> (the contract that says
/// otherwise is flagged), through the real draft service and its one writer;</item>
/// <item>a document is <b>never read into a lease of another org</b>;</item>
/// <item>the result kept with the document holds <b>no personal value</b>: the names of the fields, never a name, a fiscal code, an address;</item>
/// <item>a document that cannot be read says so with a code and writes nothing, and the manual wizard goes on.</item>
/// </list>
/// The first pipeline (a lease that already exists) is unchanged, but for what it keeps.
/// </summary>
public sealed class LeaseDocumentExtractionServiceTests
{
    // ---- the contract of an imported draft ----------------------------------------------------------------------------

    [Fact]
    public async Task TheContractOfAnImportedDraft_FillsWhatIsEmpty_AndTheResultHoldsNoPerson()
    {
        using var harness = await new LeaseDraftHarness().SeedAsync();
        var draft = await harness.CreateAsync(LeaseOrigin.Imported);
        var document = await AddDocumentAsync(harness, draft.Id);
        ServeBytes(harness);

        await CreateSut(harness, Reads(LeaseContractTextParserTests.Libero)).ExtractAsync(document.Id);

        var read = await harness.Service.GetAsync(draft.Id);
        var landlord = Assert.Single(read.Values.Landlords);
        Assert.Equal(("Mario", "Rossi", "RSSMRA80A01H501U"), (landlord.FirstName, landlord.LastName, landlord.FiscalCode));
        var tenant = Assert.Single(read.Values.Tenants);
        Assert.Equal(("Giulia", "Verdi", "IT"), (tenant.FirstName, tenant.LastName, tenant.Citizenship));
        Assert.Equal("Libero", read.Values.ContractType);
        Assert.Equal("CedolareSecca", read.Values.TaxRegime);
        Assert.Equal(800m, read.Values.MonthlyRent);
        Assert.Equal(1600m, read.Values.SecurityDeposit);
        Assert.Equal("2026-11-01", read.Values.StartDate);
        Assert.Equal(2, read.Version);

        // The citizenship of the landlord is not in the contract: the wizard asks for it, and for nothing else of the parties.
        var need = Assert.Single(read.Flow.Steps.Single(step => step.Step == 2).Needs);
        Assert.Equal("landlord_incomplete", need.Code);

        var stored = await StoredAsync(harness, document.Id);
        Assert.Equal(LeaseDocumentExtractionStatus.Completed, stored.ExtractionStatus);
        Assert.Null(stored.ExtractionError);

        // DATA RULE PROOF (ADR-009-R10, the privacy rule of NL-DOCS): the values read are in the draft, which is encrypted at rest, and not here.
        foreach (var personal in new[] { "Mario", "Rossi", "Giulia", "Verdi", "RSSMRA80A01H501U", "VRDGLI85B42F205E", "Via dei Mille", "Via Argiro", "Roma (RM)" })
            Assert.DoesNotContain(personal, stored.ExtractionResult);
        Assert.DoesNotContain("\"value\"", stored.ExtractionResult);
        Assert.Contains("fiscalCode", stored.ExtractionResult);
        Assert.Contains("2026-10-15", stored.ExtractionResult); // the one date it keeps: the day of the signature
    }

    [Fact]
    public async Task WhatThePersonTypedIsNeverOverwritten_AndTheContractThatSaysOtherwiseIsFlagged()
    {
        using var harness = await new LeaseDraftHarness().SeedAsync();
        var draft = await harness.CreateAsync(LeaseOrigin.Imported);
        var version = (await harness.SaveAsync(draft.Id, draft.Version, 3, """{"monthlyRent":950}""")).Version;
        version = (await harness.SaveAsync(draft.Id, version, 4, """{"startDate":"2026-12-01"}""")).Version;
        var document = await AddDocumentAsync(harness, draft.Id);
        ServeBytes(harness);

        await CreateSut(harness, Reads(LeaseContractTextParserTests.Libero)).ExtractAsync(document.Id);

        // DATA RULE PROOF (ADR-009-R9): the contract says 800 and the first of November; the person typed 950 and the first of December.
        var read = await harness.Service.GetAsync(draft.Id);
        Assert.Equal(950m, read.Values.MonthlyRent);
        Assert.Equal("2026-12-01", read.Values.StartDate);
        Assert.Equal(1600m, read.Values.SecurityDeposit); // what was empty is filled all the same
        Assert.Equal(version + 1, read.Version);
        var stored = await StoredAsync(harness, document.Id);
        Assert.Equal(LeaseDocumentExtractionStatus.NeedsReview, stored.ExtractionStatus);
        Assert.Contains("Conflict", stored.ExtractionResult);
    }

    [Fact]
    public async Task ADocumentOfAnotherOrg_IsNeverReadIntoTheDraft()
    {
        using var harness = await new LeaseDraftHarness().SeedAsync();
        var draft = await harness.CreateAsync(LeaseOrigin.Imported);
        var document = await AddDocumentAsync(harness, draft.Id, orgId: Guid.NewGuid());
        var reader = Reads(LeaseContractTextParserTests.Libero);

        var refused = await Assert.ThrowsAsync<DomainRuleException>(() => CreateSut(harness, reader).ExtractAsync(document.Id));

        // DATA RULE PROOF (tenancy, ADR-009-R2): the job runs without an org filter, so the document and the lease must agree on the org.
        Assert.Equal("tenant_isolation_violation", refused.Code);
        reader.Verify(r => r.ReadAsync(It.IsAny<byte[]>(), It.IsAny<CancellationToken>()), Times.Never);
        var read = await harness.Service.GetAsync(draft.Id);
        Assert.Equal(draft.Version, read.Version);
        Assert.Empty(read.Values.Landlords);
        Assert.Null(read.Values.MonthlyRent);
    }

    [Fact]
    public async Task ADocumentThatCannotBeRead_SaysSoWithACode_AndWritesNothingToTheDraft()
    {
        using var harness = await new LeaseDraftHarness().SeedAsync();
        var draft = await harness.CreateAsync(LeaseOrigin.Imported);
        var document = await AddDocumentAsync(harness, draft.Id);
        ServeBytes(harness);
        var reader = new Mock<IContractTextReader>();
        reader.Setup(r => r.ReadAsync(It.IsAny<byte[]>(), It.IsAny<CancellationToken>())).ThrowsAsync(new ContractTextUnavailableException());

        await CreateSut(harness, reader).ExtractAsync(document.Id);

        var stored = await StoredAsync(harness, document.Id);
        Assert.Equal(LeaseDocumentExtractionStatus.Failed, stored.ExtractionStatus);
        Assert.Equal("ocr_unavailable", stored.ExtractionError);
        Assert.Equal(draft.Version, (await harness.Service.GetAsync(draft.Id)).Version);
    }

    // ---- any other lease: the first pipeline ---------------------------------------------------------------------------

    /// <summary>
    /// PROOF TEST for the first pipeline (ADR-009-R4, R6): the fields certain enough go to the lease, and the result kept with the
    /// document names the fields without the names of the people, which the privacy rule of NL-DOCS no longer allows there.
    /// </summary>
    [Fact]
    public async Task ExtractAsync_OfALeaseThatExists_PopulatesItAndKeepsNoNameInTheResult()
    {
        var (db, document, lease) = await SeedLeaseAsync(monthlyRent: 0m, securityDeposit: null);
        var sut = CreateLegacySut(db, "LOCATORE: Mario Rossi\nCONDUTTORE: Giulia Bianchi\nCANONE: 1200\nDEPOSITO: 2400\nINIZIO: 01/01/2026\nFINE: 01/01/2027");

        await sut.ExtractAsync(document.Id);

        var stored = await db.LeaseDocuments.AsNoTracking().SingleAsync(d => d.Id == document.Id);
        Assert.Equal(LeaseDocumentExtractionStatus.Completed, stored.ExtractionStatus);
        Assert.NotNull(stored.ExtractionStartedAt);
        Assert.NotNull(stored.ExtractionCompletedAt);
        var populated = await db.LeaseContracts.AsNoTracking().SingleAsync(l => l.Id == lease.Id);
        Assert.Equal(1200m, populated.MonthlyRent);
        Assert.Equal(2400m, populated.SecurityDeposit);

        Assert.Contains("LandlordName", stored.ExtractionResult);
        Assert.DoesNotContain("Mario", stored.ExtractionResult);
        Assert.DoesNotContain("Giulia", stored.ExtractionResult);
        Assert.DoesNotContain("1200", stored.ExtractionResult);
    }

    /// <summary>PROOF TEST for the first pipeline (ADR-009-R9): a lease that already has its rent keeps it, and the document is flagged.</summary>
    [Fact]
    public async Task ExtractAsync_ConflictsWithUserData_FlagsForReviewNotOverwrite()
    {
        var (db, document, lease) = await SeedLeaseAsync(monthlyRent: 800m, securityDeposit: 1600m);
        var sut = CreateLegacySut(db, "LOCATORE: Paolo Verdi\nCONDUTTORE: Anna Neri\nCANONE: 1500\nDEPOSITO: 3000\nINIZIO: 01/02/2026\nFINE: 01/02/2027");

        await sut.ExtractAsync(document.Id);

        var stored = await db.LeaseDocuments.AsNoTracking().SingleAsync(d => d.Id == document.Id);
        var kept = await db.LeaseContracts.AsNoTracking().SingleAsync(l => l.Id == lease.Id);
        Assert.Equal(LeaseDocumentExtractionStatus.NeedsReview, stored.ExtractionStatus);
        Assert.Equal(800m, kept.MonthlyRent);
        Assert.Equal(1600m, kept.SecurityDeposit);
        Assert.Contains("conflicts", stored.ExtractionResult);
        Assert.DoesNotContain("Paolo", stored.ExtractionResult);
    }

    // ---- helpers ---------------------------------------------------------------------------------------------------------

    private static LeaseDocumentExtractionService CreateSut(LeaseDraftHarness harness, Mock<IContractTextReader> reader) =>
        new(harness.Db, harness.Storage.Object, reader.Object, harness.Service, NullLogger<LeaseDocumentExtractionService>.Instance);

    private static Mock<IContractTextReader> Reads(string text)
    {
        var reader = new Mock<IContractTextReader>();
        reader.Setup(r => r.ReadAsync(It.IsAny<byte[]>(), It.IsAny<CancellationToken>())).ReturnsAsync(new ContractText(text, 3, 0, false));
        return reader;
    }

    private static void ServeBytes(LeaseDraftHarness harness) =>
        harness.Storage
            .Setup(s => s.OpenReadAsync(StorageBucket.Private, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new MemoryStream(Encoding.ASCII.GetBytes("%PDF-1.4 the signed contract")));

    private static async Task<LeaseDocument> AddDocumentAsync(
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
            SizeBytes = 28,
            ExtractionStatus = LeaseDocumentExtractionStatus.Processing,
        };
        harness.Db.LeaseDocuments.Add(document);
        await harness.Db.SaveChangesAsync();
        harness.Db.ChangeTracker.Clear();
        return document;
    }

    private static async Task<LeaseDocument> StoredAsync(LeaseDraftHarness harness, Guid documentId)
    {
        await using var db = harness.SecondContext();
        return await db.LeaseDocuments.AsNoTracking().SingleAsync(d => d.Id == documentId);
    }

    private static LeaseDocumentExtractionService CreateLegacySut(AppDbContext db, string text)
    {
        var storage = new Mock<IFileStorage>();
        storage
            .Setup(s => s.OpenReadAsync(StorageBucket.Private, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new MemoryStream(Encoding.ASCII.GetBytes("%PDF-1.4 a document")));
        return new LeaseDocumentExtractionService(
            db, storage.Object, Reads(text).Object, Mock.Of<ILeaseDraftService>(), NullLogger<LeaseDocumentExtractionService>.Instance);
    }

    private static async Task<(AppDbContext Db, LeaseDocument Document, LeaseContract Lease)> SeedLeaseAsync(decimal monthlyRent, decimal? securityDeposit)
    {
        var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase($"LeaseDocumentExtraction_{Guid.NewGuid()}").Options);
        var org = new Core.Entities.Org { Id = Guid.NewGuid(), Name = "Test Org" };
        var property = new Property { Id = Guid.NewGuid(), OrgId = org.Id, OwnerId = "test-user-id", Name = "Test Property", City = "Milano" };
        var lease = new LeaseContract
        {
            Id = Guid.NewGuid(),
            OrgId = org.Id,
            PropertyId = property.Id,
            Property = property,
            StartDate = new DateTime(2026, 1, 1),
            EndDate = new DateTime(2027, 1, 1),
            MonthlyRent = monthlyRent,
            SecurityDeposit = securityDeposit,
            FiscalRegime = FiscalRegime.CedolareSecca,
            Status = LeaseStatus.Draft,
        };
        var document = new LeaseDocument
        {
            Id = Guid.NewGuid(),
            LeaseContractId = lease.Id,
            LeaseContract = lease,
            OrgId = org.Id,
            Kind = LeaseDocumentKind.Contract,
            FileName = "contract-test.pdf",
            StorageKey = "test/contract.pdf",
            UploadedBy = "test@example.com",
            SizeBytes = 1024,
            ExtractionStatus = LeaseDocumentExtractionStatus.NotStarted,
        };
        db.Orgs.Add(org);
        db.Properties.Add(property);
        db.LeaseContracts.Add(lease);
        db.LeaseDocuments.Add(document);
        await db.SaveChangesAsync();
        return (db, document, lease);
    }
}
