using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Enums;
using Casazen.Core.Repositories;
using Casazen.Core.Services;
using Casazen.Infrastructure.Services;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Services;

public class LeaseWorkflowServiceTests
{
    private readonly Mock<ILeaseContractRepository> _leaseRepo = new();
    private readonly Mock<ILeaseRegistrationRepository> _regRepo = new();
    private readonly Mock<ILeaseEventRepository> _eventRepo = new();
    private readonly Mock<ILeaseTemplateService> _templateService = new();
    private readonly Mock<ILeaseESignService> _eSignService = new();
    private readonly Mock<ILeaseRegistrationService> _regService = new();
    private readonly Mock<IPropertyRepository> _propertyRepo = new();
    private readonly LeaseWorkflowService _sut;

    private static readonly string OwnerId = "auth0|owner123";
    private static readonly Guid PropertyId = Guid.NewGuid();

    public LeaseWorkflowServiceTests()
    {
        _sut = new LeaseWorkflowService(
            _leaseRepo.Object,
            _regRepo.Object,
            _eventRepo.Object,
            _templateService.Object,
            _eSignService.Object,
            _regService.Object,
            _propertyRepo.Object,
            new Mock<ILogger<LeaseWorkflowService>>().Object);
    }

    [Fact]
    public async Task CreateDraftAsync_WithApePresent_ReturnsLeaseWithDraftStatus()
    {
        // Arrange
        var property = BuildProperty(hasApe: true);
        _propertyRepo.Setup(r => r.GetByIdAsync(PropertyId)).ReturnsAsync(property);
        _leaseRepo.Setup(r => r.AddAsync(It.IsAny<LeaseContract>()))
            .ReturnsAsync((LeaseContract l) => l);
        _eventRepo.Setup(r => r.AddAsync(It.IsAny<LeaseEvent>()))
            .ReturnsAsync((LeaseEvent e) => e);

        var request = BuildCreateRequest();

        // Act
        var result = await _sut.CreateDraftAsync(PropertyId, OwnerId, request);

        // Assert
        Assert.Equal(LeaseStatus.Draft, result.Status);
        Assert.Equal(request.MonthlyRent, result.MonthlyRent);
        Assert.Equal(request.StartDate.AddDays(30), result.RegistrationDeadline);
        Assert.Equal(request.StartDate.AddYears(10), result.DataRetentionUntil);
        Assert.False(result.ErasureRequested);
    }

    [Fact]
    public async Task CreateDraftAsync_WithoutApe_ThrowsInvalidOperationException()
    {
        // Arrange
        var property = BuildProperty(hasApe: false);
        _propertyRepo.Setup(r => r.GetByIdAsync(PropertyId)).ReturnsAsync(property);

        // Act & Assert
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _sut.CreateDraftAsync(PropertyId, OwnerId, BuildCreateRequest()));
    }

    [Fact]
    public async Task CreateDraftAsync_WhenOwnerMismatch_ThrowsUnauthorizedAccessException()
    {
        // Arrange
        var property = BuildProperty(hasApe: true, ownerId: "auth0|different-owner");
        _propertyRepo.Setup(r => r.GetByIdAsync(PropertyId)).ReturnsAsync(property);

        // Act & Assert
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            _sut.CreateDraftAsync(PropertyId, OwnerId, BuildCreateRequest()));
    }

    [Fact]
    public async Task CreateDraftAsync_WithExtraEUTenant_SetsIsExtraEUTrue()
    {
        // Arrange
        var property = BuildProperty(hasApe: true);
        _propertyRepo.Setup(r => r.GetByIdAsync(PropertyId)).ReturnsAsync(property);
        _leaseRepo.Setup(r => r.AddAsync(It.IsAny<LeaseContract>()))
            .ReturnsAsync((LeaseContract l) => l);
        _eventRepo.Setup(r => r.AddAsync(It.IsAny<LeaseEvent>()))
            .ReturnsAsync((LeaseEvent e) => e);

        var request = BuildCreateRequest(tenantCitizenship: "US");

        // Act
        var result = await _sut.CreateDraftAsync(PropertyId, OwnerId, request);

        // Assert
        var tenant = result.Parties.Single(p => p.Role == PartyRole.Tenant);
        Assert.True(tenant.IsExtraEU);
        Assert.True(result.HasExtraEUTenant);
    }

    [Fact]
    public async Task CreateDraftAsync_WithItalianTenant_SetsIsExtraEUFalse()
    {
        // Arrange
        var property = BuildProperty(hasApe: true);
        _propertyRepo.Setup(r => r.GetByIdAsync(PropertyId)).ReturnsAsync(property);
        _leaseRepo.Setup(r => r.AddAsync(It.IsAny<LeaseContract>()))
            .ReturnsAsync((LeaseContract l) => l);
        _eventRepo.Setup(r => r.AddAsync(It.IsAny<LeaseEvent>()))
            .ReturnsAsync((LeaseEvent e) => e);

        var request = BuildCreateRequest(tenantCitizenship: "IT");

        // Act
        var result = await _sut.CreateDraftAsync(PropertyId, OwnerId, request);

        // Assert
        var tenant = result.Parties.Single(p => p.Role == PartyRole.Tenant);
        Assert.False(tenant.IsExtraEU);
        Assert.False(result.HasExtraEUTenant);
    }

    [Fact]
    public async Task InitiateSigningAsync_WhenStatusIsDraft_TransitionsToAwaitingSignature()
    {
        // Arrange
        var lease = BuildLease(LeaseStatus.Draft);
        _leaseRepo.Setup(r => r.GetByIdWithDetailsAsync(lease.Id)).ReturnsAsync(lease);
        _leaseRepo.Setup(r => r.UpdateAsync(It.IsAny<LeaseContract>()))
            .ReturnsAsync((LeaseContract l) => l);
        _eventRepo.Setup(r => r.AddAsync(It.IsAny<LeaseEvent>()))
            .ReturnsAsync((LeaseEvent e) => e);
        _templateService.Setup(s => s.GeneratePdfAsync(lease))
            .ReturnsAsync([0x25, 0x50, 0x44, 0x46]); // %PDF header
        _eSignService.Setup(s => s.InitiateSigningAsync(lease, It.IsAny<byte[]>()))
            .ReturnsAsync([]);

        // Act
        var result = await _sut.InitiateSigningAsync(lease.Id, OwnerId);

        // Assert
        Assert.Equal(LeaseStatus.AwaitingSignature, result.Status);
    }

    [Fact]
    public async Task InitiateSigningAsync_WhenStatusIsNotDraft_ThrowsInvalidOperationException()
    {
        // Arrange
        var lease = BuildLease(LeaseStatus.Signed);
        _leaseRepo.Setup(r => r.GetByIdWithDetailsAsync(lease.Id)).ReturnsAsync(lease);

        // Act & Assert
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _sut.InitiateSigningAsync(lease.Id, OwnerId));
    }

    [Fact]
    public async Task TriggerRegistrationAsync_WhenStatusIsSigned_SubmitsAndTransitionsStatus()
    {
        // Arrange
        var lease = BuildLease(LeaseStatus.Signed);
        _leaseRepo.Setup(r => r.GetByIdWithDetailsAsync(lease.Id)).ReturnsAsync(lease);
        _leaseRepo.Setup(r => r.UpdateAsync(It.IsAny<LeaseContract>()))
            .ReturnsAsync((LeaseContract l) => l);
        _regRepo.Setup(r => r.AddAsync(It.IsAny<LeaseRegistration>()))
            .ReturnsAsync((LeaseRegistration r) => r);
        _eventRepo.Setup(r => r.AddAsync(It.IsAny<LeaseEvent>()))
            .ReturnsAsync((LeaseEvent e) => e);
        _regService.Setup(s => s.SubmitRegistrationAsync(lease))
            .ReturnsAsync("RLI-EXTERNAL-001");

        // Act
        var registration = await _sut.TriggerRegistrationAsync(lease.Id, OwnerId);

        // Assert
        Assert.Equal(RegistrationStatus.SentToProvider, registration.Status);
        Assert.Equal("RLI-EXTERNAL-001", registration.ExternalRegistrationId);
        Assert.NotNull(registration.SubmittedAt);
    }

    [Fact]
    public async Task TriggerRegistrationAsync_WhenStatusIsNotSigned_ThrowsInvalidOperationException()
    {
        // Arrange
        var lease = BuildLease(LeaseStatus.Draft);
        _leaseRepo.Setup(r => r.GetByIdWithDetailsAsync(lease.Id)).ReturnsAsync(lease);

        // Act & Assert
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _sut.TriggerRegistrationAsync(lease.Id, OwnerId));
    }

    // Helpers

    private static Property BuildProperty(bool hasApe, string? ownerId = null) => new()
    {
        Id = PropertyId,
        OwnerId = ownerId ?? OwnerId,
        Name = "Test Property",
        PropertyDocuments = hasApe
            ? [new PropertyDocument { DocumentType = DocumentType.Ape, FileName = "ape.pdf", StorageUrl = "/ape.pdf", UploadedBy = OwnerId }]
            : []
    };

    private static CreateLeaseRequest BuildCreateRequest(string tenantCitizenship = "IT") => new(
        FiscalRegime: FiscalRegime.CedolareSecca,
        StartDate: new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc),
        EndDate: new DateTime(2030, 8, 31, 0, 0, 0, DateTimeKind.Utc),
        MonthlyRent: 1200.00m,
        Parties:
        [
            new CreatePartyRequest(PartyRole.Landlord, "Mario", "Rossi", "RSSMRA80A01H501Z", "IT", "mario@example.com"),
            new CreatePartyRequest(PartyRole.Tenant, "John", "Doe", "DOEJHN90B02Z123X", tenantCitizenship, "john@example.com")
        ]);

    private static LeaseContract BuildLease(LeaseStatus status)
    {
        var property = new Property { Id = PropertyId, OwnerId = OwnerId, Name = "Test Property" };
        return new LeaseContract
        {
            Id = Guid.NewGuid(),
            PropertyId = PropertyId,
            Property = property,
            Status = status,
            FiscalRegime = FiscalRegime.CedolareSecca,
            StartDate = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc),
            EndDate = new DateTime(2030, 8, 31, 0, 0, 0, DateTimeKind.Utc),
            MonthlyRent = 1200m,
            Parties = []
        };
    }
}
