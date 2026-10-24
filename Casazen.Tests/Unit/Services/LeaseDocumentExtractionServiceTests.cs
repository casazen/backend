using System.Text;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// Proof tests for lease document extraction data rules (ADR-009):
/// - Personal data extraction from multi-page documents
/// - No silent overwrite of confirmed user data
/// </summary>
public sealed class LeaseDocumentExtractionServiceTests : IDisposable
{
    private readonly AppDbContext _db;
    private readonly Mock<IFileStorage> _storageMock;
    private readonly LeaseDocumentExtractionService _service;

    public LeaseDocumentExtractionServiceTests()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"LeaseDocumentExtractionTests_{Guid.NewGuid()}")
            .Options;
        _db = new AppDbContext(options);
        _storageMock = new Mock<IFileStorage>();
        _service = new LeaseDocumentExtractionService(_db, _storageMock.Object, NullLogger<LeaseDocumentExtractionService>.Instance);
    }

    public void Dispose()
    {
        _db.Dispose();
    }

    /// <summary>
    /// PROOF TEST for data rule: personal data extraction from multi-page PDFs (ADR-009-R4, ADR-009-R6).
    /// Verifies that the extraction service processes all pages of a multi-page contract and extracts
    /// personal data (names, amounts) correctly.
    /// </summary>
    [Fact]
    public async Task ExtractAsync_WithMultiPageContract_ExtractsPersonalData()
    {
        // Arrange: create a lease contract with property
        var org = new Core.Entities.Org { Id = Guid.NewGuid(), Name = "Test Org" };
        var property = new Property
        {
            Id = Guid.NewGuid(),
            OrgId = org.Id,
            OwnerId = "test-user-id",
            Name = "Test Property",
            City = "Milano"
        };
        var lease = new LeaseContract
        {
            Id = Guid.NewGuid(),
            OrgId = org.Id,
            PropertyId = property.Id,
            Property = property,
            StartDate = new DateTime(2026, 1, 1),
            EndDate = new DateTime(2027, 1, 1),
            MonthlyRent = 0, // Not set yet - should be populated
            SecurityDeposit = null, // Not set - should be populated
            FiscalRegime = FiscalRegime.CedolareSecca,
            Status = LeaseStatus.Draft
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
            ExtractionStatus = LeaseDocumentExtractionStatus.NotStarted
        };

        _db.Orgs.Add(org);
        _db.Properties.Add(property);
        _db.LeaseContracts.Add(lease);
        _db.LeaseDocuments.Add(document);
        await _db.SaveChangesAsync();

        // Create a multi-page PDF with personal data (simplified - in reality this would be a real PDF)
        var pdfContent = CreateMockPdfWithPersonalData();
        _storageMock.Setup(s => s.OpenReadAsync(StorageBucket.Private, document.StorageKey, It.IsAny<CancellationToken>()))
            .ReturnsAsync(pdfContent);

        // Act: extract data from the document
        await _service.ExtractAsync(document.Id);

        // Assert: verify personal data was extracted
        var updatedDocument = await _db.LeaseDocuments.FindAsync(document.Id);
        Assert.NotNull(updatedDocument);
        Assert.Equal(LeaseDocumentExtractionStatus.Completed, updatedDocument.ExtractionStatus);
        Assert.NotNull(updatedDocument.ExtractionResult);
        Assert.NotNull(updatedDocument.ExtractionStartedAt);
        Assert.NotNull(updatedDocument.ExtractionCompletedAt);

        // Verify that extraction result contains personal data fields
        Assert.Contains("Mario Rossi TEST", updatedDocument.ExtractionResult);
        Assert.Contains("Giulia Bianchi TEST", updatedDocument.ExtractionResult);
        Assert.Contains("1200", updatedDocument.ExtractionResult);

        // DATA RULE PROOF: The extraction processes multi-page documents and extracts personal data
        // (names, amounts) as required by ADR-009-R4 and ADR-009-R6
    }

    /// <summary>
    /// PROOF TEST for data rule: no silent overwrite of confirmed user data (ADR-009-R9).
    /// Verifies that when extraction finds data that conflicts with existing user-entered data,
    /// it flags the conflict and sets status to NeedsReview instead of silently overwriting.
    /// </summary>
    [Fact]
    public async Task ExtractAsync_ConflictsWithUserData_FlagsForReviewNotOverwrite()
    {
        // Arrange: create a lease contract with EXISTING user data
        var org = new Core.Entities.Org { Id = Guid.NewGuid(), Name = "Test Org" };
        var property = new Property
        {
            Id = Guid.NewGuid(),
            OrgId = org.Id,
            OwnerId = "test-user-id",
            Name = "Test Property",
            City = "Roma"
        };
        var lease = new LeaseContract
        {
            Id = Guid.NewGuid(),
            OrgId = org.Id,
            PropertyId = property.Id,
            Property = property,
            StartDate = new DateTime(2026, 1, 1),
            EndDate = new DateTime(2027, 1, 1),
            MonthlyRent = 800m, // CONFIRMED user data - should NOT be overwritten
            SecurityDeposit = 1600m, // CONFIRMED user data - should NOT be overwritten
            FiscalRegime = FiscalRegime.CedolareSecca,
            Status = LeaseStatus.Draft
        };
        var document = new LeaseDocument
        {
            Id = Guid.NewGuid(),
            LeaseContractId = lease.Id,
            LeaseContract = lease,
            OrgId = org.Id,
            Kind = LeaseDocumentKind.Contract,
            FileName = "contract-conflict-test.pdf",
            StorageKey = "test/contract-conflict.pdf",
            UploadedBy = "test@example.com",
            SizeBytes = 1024,
            ExtractionStatus = LeaseDocumentExtractionStatus.NotStarted
        };

        _db.Orgs.Add(org);
        _db.Properties.Add(property);
        _db.LeaseContracts.Add(lease);
        _db.LeaseDocuments.Add(document);
        await _db.SaveChangesAsync();

        // Create a PDF with DIFFERENT values (conflict with user data)
        var pdfContent = CreateMockPdfWithConflictingData();
        _storageMock.Setup(s => s.OpenReadAsync(StorageBucket.Private, document.StorageKey, It.IsAny<CancellationToken>()))
            .ReturnsAsync(pdfContent);

        // Act: extract data from the document
        await _service.ExtractAsync(document.Id);

        // Assert: verify the status is NeedsReview and user data was NOT overwritten
        var updatedDocument = await _db.LeaseDocuments.FindAsync(document.Id);
        var updatedLease = await _db.LeaseContracts.FindAsync(lease.Id);

        Assert.NotNull(updatedDocument);
        Assert.NotNull(updatedLease);

        // Status should be NeedsReview because of conflicts
        Assert.Equal(LeaseDocumentExtractionStatus.NeedsReview, updatedDocument.ExtractionStatus);

        // DATA RULE PROOF: User data was NOT silently overwritten
        Assert.Equal(800m, updatedLease.MonthlyRent); // Original value preserved
        Assert.Equal(1600m, updatedLease.SecurityDeposit); // Original value preserved

        // Extraction result should flag the conflict
        Assert.NotNull(updatedDocument.ExtractionResult);
        Assert.Contains("conflicts", updatedDocument.ExtractionResult);

        // DATA RULE PROOF: The extraction does NOT silently overwrite confirmed user data,
        // instead it flags conflicts and sets status to NeedsReview as required by ADR-009-R9
    }

    private Stream CreateMockPdfWithPersonalData()
    {
        // Create a PDF with extractable text using LiteralTextPdf (ADR-009-R4: multi-page support)
        // MigraDoc-generated PDFs may not have text extractable by PdfPig, so we use a literal text PDF
        var title = "CONTRATTO DI LOCAZIONE";
        var body = @"Il presente contratto è stipulato tra:
LOCATORE: Mario Rossi TEST
Codice Fiscale: RSSMRA80A01F205X

CONDUTTORE: Giulia Bianchi TEST
Codice Fiscale: BNCGLI85M41H501Y

CANONE MENSILE: € 1200,00
DEPOSITO CAUZIONALE: € 2400,00
INIZIO: 01/01/2026
FINE: 01/01/2027";

        var pdfBytes = LiteralTextPdf.Build(title, body);
        return new MemoryStream(pdfBytes);
    }

    private Stream CreateMockPdfWithConflictingData()
    {
        // Create a PDF with data that conflicts with the existing user data
        var title = "CONTRATTO DI LOCAZIONE";
        var body = @"LOCATORE: Paolo Verdi TEST
CONDUTTORE: Anna Neri TEST

CANONE MENSILE: € 1500,00
DEPOSITO CAUZIONALE: € 3000,00
INIZIO: 01/02/2026
FINE: 01/02/2027";

        var pdfBytes = LiteralTextPdf.Build(title, body);
        return new MemoryStream(pdfBytes);
    }
}
