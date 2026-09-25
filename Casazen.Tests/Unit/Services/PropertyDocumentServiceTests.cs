using Casazen.Core.Entities;
using Casazen.Core.Enums;
using Casazen.Core.Repositories;
using Casazen.Core.Services;
using Casazen.Infrastructure.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Services;

public class PropertyDocumentServiceTests
{
    private readonly Mock<IPropertyDocumentRepository> _mockDocumentRepository;
    private readonly Mock<IImageStorageService> _mockStorageService;
    private readonly Mock<IPropertyRepository> _mockPropertyRepository;
    private readonly Mock<IApeComplianceService> _mockApeCompliance;
    private readonly Mock<IPropertyComplianceStatusService> _complianceStatus = new();
    private readonly PropertyDocumentService _service;

    public PropertyDocumentServiceTests()
    {
        _mockDocumentRepository = new Mock<IPropertyDocumentRepository>();
        _mockStorageService = new Mock<IImageStorageService>();
        _mockPropertyRepository = new Mock<IPropertyRepository>();
        _mockApeCompliance = new Mock<IApeComplianceService>();
        _service = new PropertyDocumentService(
            _mockDocumentRepository.Object,
            _mockStorageService.Object,
            _mockPropertyRepository.Object,
            _mockApeCompliance.Object,
            _complianceStatus.Object,
            new Mock<ILogger<PropertyDocumentService>>().Object);
    }

    [Fact]
    public async Task UploadDocumentAsync_WithValidPropertyAndFile_ReturnsDocument()
    {
        // Arrange
        var propertyId = Guid.NewGuid();
        var storageUrl = "/uploads/properties/test-doc.pdf";
        var mockFile = CreateMockFile("test-doc.pdf");
        var expectedDocument = new PropertyDocument
        {
            Id = Guid.NewGuid(),
            PropertyId = propertyId,
            FileName = "test-doc.pdf",
            StorageUrl = storageUrl,
            DocumentType = DocumentType.CinCertificate,
            UploadedBy = "user@example.com",
            UploadedAt = DateTime.UtcNow
        };

        var orgId = Guid.NewGuid();
        _mockPropertyRepository.Setup(x => x.GetOrgIdAsync(propertyId)).ReturnsAsync(orgId);
        _mockStorageService.Setup(x => x.ValidateDocument(mockFile.Object)).Returns(true);
        _mockStorageService.Setup(x => x.UploadDocumentAsync(mockFile.Object, propertyId)).ReturnsAsync(storageUrl);
        _mockDocumentRepository.Setup(x => x.AddAsync(It.IsAny<PropertyDocument>())).ReturnsAsync(expectedDocument);

        // Act
        var result = await _service.UploadDocumentAsync(propertyId, mockFile.Object, DocumentType.CinCertificate, "user@example.com");

        // Assert
        Assert.NotNull(result);
        Assert.Equal(expectedDocument.Id, result.Id);
        Assert.Equal(propertyId, result.PropertyId);
        // TN-2: the stored row carries the property's org (tenant filter + FK).
        _mockDocumentRepository.Verify(x => x.AddAsync(It.Is<PropertyDocument>(d => d.OrgId == orgId && d.PropertyId == propertyId)), Times.Once);
    }

    [Fact]
    public async Task UploadDocumentAsync_WithNonExistentProperty_ThrowsInvalidOperationException()
    {
        // Arrange
        var propertyId = Guid.NewGuid();
        var mockFile = CreateMockFile("test-doc.pdf");

        _mockPropertyRepository.Setup(x => x.GetOrgIdAsync(propertyId)).ReturnsAsync((Guid?)null);

        // Act & Assert
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _service.UploadDocumentAsync(propertyId, mockFile.Object, DocumentType.CinCertificate, "user@example.com"));

        _mockStorageService.Verify(x => x.UploadDocumentAsync(It.IsAny<IFormFile>(), It.IsAny<Guid>()), Times.Never);
        _mockApeCompliance.Verify(x => x.EnsureUploadedFileIsOfficialApeAsync(It.IsAny<IFormFile>()), Times.Never);
        _mockDocumentRepository.Verify(x => x.AddAsync(It.IsAny<PropertyDocument>()), Times.Never);
    }

    [Fact]
    public async Task UploadDocumentAsync_WithValidFile_CallsStorageServiceWithCorrectParams()
    {
        // Arrange
        var propertyId = Guid.NewGuid();
        var mockFile = CreateMockFile("floor-plan.png");
        var storageUrl = "/uploads/properties/floor-plan.png";
        var savedDocument = new PropertyDocument { Id = Guid.NewGuid(), PropertyId = propertyId };

        _mockPropertyRepository.Setup(x => x.GetOrgIdAsync(propertyId)).ReturnsAsync(Guid.NewGuid());
        _mockStorageService.Setup(x => x.ValidateDocument(mockFile.Object)).Returns(true);
        _mockStorageService.Setup(x => x.UploadDocumentAsync(mockFile.Object, propertyId)).ReturnsAsync(storageUrl);
        _mockDocumentRepository.Setup(x => x.AddAsync(It.IsAny<PropertyDocument>())).ReturnsAsync(savedDocument);

        // Act
        await _service.UploadDocumentAsync(propertyId, mockFile.Object, DocumentType.FloorPlan, "owner@example.com");

        // Assert
        _mockStorageService.Verify(x => x.UploadDocumentAsync(mockFile.Object, propertyId), Times.Once);
    }

    [Fact]
    public async Task GetByPropertyIdAsync_WithDocuments_ReturnsDocuments()
    {
        // Arrange
        var propertyId = Guid.NewGuid();
        var documents = new List<PropertyDocument>
        {
            new() { Id = Guid.NewGuid(), PropertyId = propertyId, FileName = "doc1.pdf" },
            new() { Id = Guid.NewGuid(), PropertyId = propertyId, FileName = "doc2.pdf" }
        };

        _mockDocumentRepository.Setup(x => x.GetByPropertyIdAsync(propertyId)).ReturnsAsync(documents);

        // Act
        var result = await _service.GetByPropertyIdAsync(propertyId);

        // Assert
        Assert.NotNull(result);
        Assert.Equal(2, result.Count());
        _mockDocumentRepository.Verify(x => x.GetByPropertyIdAsync(propertyId), Times.Once);
    }

    [Fact]
    public async Task GetByPropertyIdAsync_WithNoDocuments_ReturnsEmptyList()
    {
        // Arrange
        var propertyId = Guid.NewGuid();

        _mockDocumentRepository.Setup(x => x.GetByPropertyIdAsync(propertyId)).ReturnsAsync(new List<PropertyDocument>());

        // Act
        var result = await _service.GetByPropertyIdAsync(propertyId);

        // Assert
        Assert.NotNull(result);
        Assert.Empty(result);
        _mockDocumentRepository.Verify(x => x.GetByPropertyIdAsync(propertyId), Times.Once);
    }

    [Fact]
    public async Task DeleteDocumentAsync_WithExistingDocument_DeletesFromRepositoryAndStorage()
    {
        // Arrange
        var documentId = Guid.NewGuid();
        var document = new PropertyDocument
        {
            Id = documentId,
            PropertyId = Guid.NewGuid(),
            FileName = "doc.pdf",
            StorageUrl = "properties/p/documents/doc.pdf"
        };

        _mockDocumentRepository.Setup(x => x.GetByIdAsync(documentId)).ReturnsAsync(document);
        _mockStorageService.Setup(x => x.DeleteDocumentAsync(document.StorageUrl)).Returns(Task.CompletedTask);
        _mockDocumentRepository.Setup(x => x.DeleteAsync(documentId)).Returns(Task.CompletedTask);

        // Act
        await _service.DeleteDocumentAsync(documentId);

        // Assert
        _mockStorageService.Verify(x => x.DeleteDocumentAsync(document.StorageUrl), Times.Once);
        _mockDocumentRepository.Verify(x => x.DeleteAsync(documentId), Times.Once);
        // CO-06 (A5-20): an active property that loses a required document is suspended.
        _complianceStatus.Verify(x => x.ReevaluateAsync(document.PropertyId, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task DeleteDocumentAsync_WithNonExistentDocument_ThrowsInvalidOperationException()
    {
        // Arrange
        var documentId = Guid.NewGuid();

        _mockDocumentRepository.Setup(x => x.GetByIdAsync(documentId)).ReturnsAsync((PropertyDocument?)null);

        // Act & Assert
        await Assert.ThrowsAsync<InvalidOperationException>(() => _service.DeleteDocumentAsync(documentId));

        _mockStorageService.Verify(x => x.DeleteDocumentAsync(It.IsAny<string>()), Times.Never);
        _mockDocumentRepository.Verify(x => x.DeleteAsync(It.IsAny<Guid>()), Times.Never);
    }

    [Fact]
    public async Task UploadDocumentAsync_WithApeType_ValidatesOfficialCertificateContent()
    {
        var propertyId = Guid.NewGuid();
        var mockFile = CreateMockFile("ape.pdf");
        var saved = new PropertyDocument { Id = Guid.NewGuid(), PropertyId = propertyId, DocumentType = DocumentType.Ape };

        _mockPropertyRepository.Setup(x => x.GetOrgIdAsync(propertyId)).ReturnsAsync(Guid.NewGuid());
        _mockStorageService.Setup(x => x.ValidateDocument(mockFile.Object)).Returns(true);
        _mockStorageService.Setup(x => x.UploadDocumentAsync(mockFile.Object, propertyId)).ReturnsAsync("/ape.pdf");
        _mockApeCompliance.Setup(x => x.EnsureUploadedFileIsOfficialApeAsync(mockFile.Object)).Returns(Task.CompletedTask);
        _mockDocumentRepository.Setup(x => x.AddAsync(It.IsAny<PropertyDocument>())).ReturnsAsync(saved);

        await _service.UploadDocumentAsync(propertyId, mockFile.Object, DocumentType.Ape, "owner@example.com");

        _mockApeCompliance.Verify(x => x.EnsureUploadedFileIsOfficialApeAsync(mockFile.Object), Times.Once);
    }

    [Fact]
    public async Task UploadDocumentAsync_WithFakeApePdf_DoesNotStoreFile()
    {
        var propertyId = Guid.NewGuid();
        var mockFile = CreateMockFile("not-an-ape.pdf");

        _mockPropertyRepository.Setup(x => x.GetOrgIdAsync(propertyId)).ReturnsAsync(Guid.NewGuid());
        _mockStorageService.Setup(x => x.ValidateDocument(mockFile.Object)).Returns(true);
        _mockApeCompliance.Setup(x => x.EnsureUploadedFileIsOfficialApeAsync(mockFile.Object))
            .ThrowsAsync(ApeComplianceException.InvalidContent());

        var ex = await Assert.ThrowsAsync<ApeComplianceException>(() =>
            _service.UploadDocumentAsync(propertyId, mockFile.Object, DocumentType.Ape, "owner@example.com"));

        Assert.Equal(ApeComplianceException.InvalidContentCode, ex.Code);
        _mockStorageService.Verify(x => x.UploadDocumentAsync(It.IsAny<IFormFile>(), It.IsAny<Guid>()), Times.Never);
        _mockDocumentRepository.Verify(x => x.AddAsync(It.IsAny<PropertyDocument>()), Times.Never);
    }

    private static Mock<IFormFile> CreateMockFile(string fileName)
    {
        var mockFile = new Mock<IFormFile>();
        mockFile.Setup(f => f.FileName).Returns(fileName);
        mockFile.Setup(f => f.Length).Returns(1024);
        return mockFile;
    }
}
