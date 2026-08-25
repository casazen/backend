using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Enums;
using Casazen.Core.Options;
using Casazen.Core.Repositories;
using Casazen.Core.Services;
using Casazen.Infrastructure.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
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
    private readonly Mock<ILeaseRegistrationAuthorizationRepository> _authRepo = new();
    private readonly Mock<IApeComplianceService> _apeCompliance = new();
    private readonly LeaseWorkflowService _sut;

    private static readonly string OwnerId = "auth0|owner123";
    private static readonly Guid PropertyId = Guid.NewGuid();
    private static readonly RegistrationAuthorizationRequest ValidAuth =
        new("2026-08-rli-delega-bozza", true);

    public LeaseWorkflowServiceTests()
    {
        _authRepo.Setup(r => r.AddAsync(It.IsAny<LeaseRegistrationAuthorization>()))
            .ReturnsAsync((LeaseRegistrationAuthorization a) => a);
        _apeCompliance.Setup(s => s.EnsurePropertyHasValidApeAsync(It.IsAny<Guid>()))
            .Returns(Task.CompletedTask);
        _sut = new LeaseWorkflowService(
            _leaseRepo.Object,
            _regRepo.Object,
            _eventRepo.Object,
            _templateService.Object,
            _eSignService.Object,
            _regService.Object,
            _propertyRepo.Object,
            _authRepo.Object,
            _apeCompliance.Object,
            Options.Create(new RliOptions { TosVersion = "2026-08-rli-delega-bozza" }),
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
        _apeCompliance.Setup(s => s.EnsurePropertyHasValidApeAsync(PropertyId))
            .ThrowsAsync(ApeComplianceException.Required());

        // Act & Assert
        var ex = await Assert.ThrowsAsync<ApeComplianceException>(() =>
            _sut.CreateDraftAsync(PropertyId, OwnerId, BuildCreateRequest()));
        Assert.Equal(ApeComplianceException.RequiredCode, ex.Code);
    }

    [Fact]
    public async Task CreateDraftAsync_WhenApeContentIsNotOfficialCertificate_ThrowsInvalidApeException()
    {
        var property = BuildProperty(hasApe: true);
        property.PropertyDocuments.Clear();
        _propertyRepo.Setup(r => r.GetByIdAsync(PropertyId)).ReturnsAsync(property);
        _apeCompliance.Setup(s => s.EnsurePropertyHasValidApeAsync(PropertyId))
            .ThrowsAsync(ApeComplianceException.InvalidContent());

        var ex = await Assert.ThrowsAsync<ApeComplianceException>(() =>
            _sut.CreateDraftAsync(PropertyId, OwnerId, BuildCreateRequest()));
        Assert.Equal(ApeComplianceException.InvalidContentCode, ex.Code);
    }

    [Fact]
    public async Task CreateDraftAsync_WhenDocumentsNotLoadedOnProperty_StillChecksStoredApe()
    {
        var property = BuildProperty(hasApe: false);
        _propertyRepo.Setup(r => r.GetByIdAsync(PropertyId)).ReturnsAsync(property);
        _leaseRepo.Setup(r => r.AddAsync(It.IsAny<LeaseContract>()))
            .ReturnsAsync((LeaseContract l) => l);
        _eventRepo.Setup(r => r.AddAsync(It.IsAny<LeaseEvent>()))
            .ReturnsAsync((LeaseEvent e) => e);

        var result = await _sut.CreateDraftAsync(PropertyId, OwnerId, BuildCreateRequest());

        Assert.Equal(LeaseStatus.Draft, result.Status);
        _apeCompliance.Verify(s => s.EnsurePropertyHasValidApeAsync(PropertyId), Times.Once);
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
            .ReturnsAsync(new SigningSessionResult("session-abc", []));

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
    public async Task InitiateSigningAsync_WhenApeWasDeletedAfterDraft_BlocksBeforeCreatingSigningSession()
    {
        var lease = BuildLease(LeaseStatus.Draft);
        _leaseRepo.Setup(r => r.GetByIdWithDetailsAsync(lease.Id)).ReturnsAsync(lease);
        _apeCompliance.Setup(s => s.EnsurePropertyHasValidApeAsync(lease.PropertyId))
            .ThrowsAsync(ApeComplianceException.Required());

        var ex = await Assert.ThrowsAsync<ApeComplianceException>(() =>
            _sut.InitiateSigningAsync(lease.Id, OwnerId));

        Assert.Equal(ApeComplianceException.RequiredCode, ex.Code);
        _templateService.Verify(s => s.GeneratePdfAsync(It.IsAny<LeaseContract>()), Times.Never);
        _eSignService.Verify(s => s.InitiateSigningAsync(It.IsAny<LeaseContract>(), It.IsAny<byte[]>()), Times.Never);
        _leaseRepo.Verify(r => r.UpdateAsync(It.IsAny<LeaseContract>()), Times.Never);
    }

    [Fact]
    public async Task TriggerRegistrationAsync_WhenStatusIsSigned_SubmitsAndTransitionsStatus()
    {
        // Arrange
        var lease = BuildLease(LeaseStatus.Signed);
        _leaseRepo.Setup(r => r.GetByIdWithDetailsAsync(lease.Id)).ReturnsAsync(lease);
        _leaseRepo.Setup(r => r.UpdateAsync(It.IsAny<LeaseContract>()))
            .ReturnsAsync((LeaseContract l) => l);
        _regRepo.Setup(r => r.GetByLeaseIdAsync(lease.Id)).ReturnsAsync((LeaseRegistration?)null);
        _regRepo.Setup(r => r.AddAsync(It.IsAny<LeaseRegistration>()))
            .ReturnsAsync((LeaseRegistration r) => r);
        _eventRepo.Setup(r => r.AddAsync(It.IsAny<LeaseEvent>()))
            .ReturnsAsync((LeaseEvent e) => e);
        _regService.Setup(s => s.SubmitRegistrationAsync(lease))
            .ReturnsAsync("RLI-EXTERNAL-001");

        // Act
        var registration = await _sut.TriggerRegistrationAsync(lease.Id, OwnerId, ValidAuth);

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
            _sut.TriggerRegistrationAsync(lease.Id, OwnerId, ValidAuth));
    }

    [Fact]
    public async Task TriggerRegistrationAsync_WhenApeWasDeletedAfterSigning_BlocksBeforeAuthorizationAndFiling()
    {
        var lease = BuildLease(LeaseStatus.Signed);
        _leaseRepo.Setup(r => r.GetByIdWithDetailsAsync(lease.Id)).ReturnsAsync(lease);
        _regRepo.Setup(r => r.GetByLeaseIdAsync(lease.Id)).ReturnsAsync((LeaseRegistration?)null);
        _apeCompliance.Setup(s => s.EnsurePropertyHasValidApeAsync(lease.PropertyId))
            .ThrowsAsync(ApeComplianceException.Required());

        var ex = await Assert.ThrowsAsync<ApeComplianceException>(() =>
            _sut.TriggerRegistrationAsync(lease.Id, OwnerId, ValidAuth));

        Assert.Equal(ApeComplianceException.RequiredCode, ex.Code);
        _authRepo.Verify(r => r.AddAsync(It.IsAny<LeaseRegistrationAuthorization>()), Times.Never);
        _regService.Verify(s => s.SubmitRegistrationAsync(It.IsAny<LeaseContract>()), Times.Never);
        _regRepo.Verify(r => r.AddAsync(It.IsAny<LeaseRegistration>()), Times.Never);
        _leaseRepo.Verify(r => r.UpdateAsync(It.IsAny<LeaseContract>()), Times.Never);
    }

    [Fact]
    public async Task GetRegistrationReceiptAsync_WhenNotRegistered_ThrowsReceiptNotAvailable()
    {
        var lease = BuildLease(LeaseStatus.SentToProvider);
        _leaseRepo.Setup(r => r.GetByIdWithDetailsAsync(lease.Id)).ReturnsAsync(lease);
        _regRepo.Setup(r => r.GetByLeaseIdAsync(lease.Id)).ReturnsAsync(new LeaseRegistration
        {
            LeaseContractId = lease.Id,
            Status = RegistrationStatus.SentToProvider,
            ExternalRegistrationId = "RLI-WAIT",
        });

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _sut.GetRegistrationReceiptAsync(lease.Id, OwnerId));
        Assert.Equal("Receipt is not available yet.", ex.Message);
    }

    [Fact]
    public async Task GetRegistrationReceiptAsync_WhenRegistered_ReturnsProviderStream()
    {
        var lease = BuildLease(LeaseStatus.Registered);
        _leaseRepo.Setup(r => r.GetByIdWithDetailsAsync(lease.Id)).ReturnsAsync(lease);
        _regRepo.Setup(r => r.GetByLeaseIdAsync(lease.Id)).ReturnsAsync(new LeaseRegistration
        {
            LeaseContractId = lease.Id,
            Status = RegistrationStatus.Registered,
            ExternalRegistrationId = "RLI-OK",
            ReceiptStoragePath = "/receipts/ignored-by-stub.pdf",
        });
        _regService.Setup(s => s.DownloadReceiptAsync("RLI-OK"))
            .ReturnsAsync(new MemoryStream("pdf"u8.ToArray()));

        await using var stream = await _sut.GetRegistrationReceiptAsync(lease.Id, OwnerId);
        using var reader = new StreamReader(stream);
        Assert.Equal("pdf", await reader.ReadToEndAsync());
        _regService.Verify(s => s.DownloadReceiptAsync("RLI-OK"), Times.Once);
    }

    [Fact]
    public async Task TriggerRegistrationAsync_WhenAlreadySubmitted_ThrowsInvalidOperationException()
    {
        // Arrange
        var lease = BuildLease(LeaseStatus.Signed);
        _leaseRepo.Setup(r => r.GetByIdWithDetailsAsync(lease.Id)).ReturnsAsync(lease);
        _regRepo.Setup(r => r.GetByLeaseIdAsync(lease.Id))
            .ReturnsAsync(new LeaseRegistration { LeaseContractId = lease.Id, Status = RegistrationStatus.SentToProvider });

        // Act & Assert
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _sut.TriggerRegistrationAsync(lease.Id, OwnerId, ValidAuth));
    }

    [Fact]
    public async Task InitiateSigningAsync_WhenAlreadyAwaitingSignature_ThrowsInvalidOperationException()
    {
        // Arrange
        var lease = BuildLease(LeaseStatus.AwaitingSignature);
        _leaseRepo.Setup(r => r.GetByIdWithDetailsAsync(lease.Id)).ReturnsAsync(lease);

        // Act & Assert
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _sut.InitiateSigningAsync(lease.Id, OwnerId));
    }

    [Fact]
    public async Task GetLeaseDetailAsync_WhenWrongOwner_ReturnsNull()
    {
        // Arrange
        var lease = BuildLease(LeaseStatus.Draft); // Property.OwnerId = OwnerId
        _leaseRepo.Setup(r => r.GetByIdWithDetailsAsync(lease.Id)).ReturnsAsync(lease);

        // Act
        var result = await _sut.GetLeaseDetailAsync(lease.Id, "auth0|different-owner");

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public async Task HandleESignEventAsync_WhenNoMatchingSession_LogsAndReturns()
    {
        // Arrange
        var esignEvent = new ESignEvent("unknown-session", "all_signed", null, true, null);
        _eSignService.Setup(s => s.ParseWebhookEventAsync("payload")).ReturnsAsync(esignEvent);
        _leaseRepo.Setup(r => r.GetByExternalSigningSessionIdAsync("unknown-session"))
            .ReturnsAsync((LeaseContract?)null);

        // Act — should not throw
        await _sut.HandleESignEventAsync("payload");

        // Assert — no update calls made
        _leaseRepo.Verify(r => r.UpdateAsync(It.IsAny<LeaseContract>()), Times.Never);
    }

    [Fact]
    public async Task HandleESignEventAsync_WhenAllSigned_TransitionsLeaseToSigned()
    {
        // Arrange
        var lease = BuildLease(LeaseStatus.AwaitingSignature);
        lease.ExternalSigningSessionId = "session-xyz";
        var esignEvent = new ESignEvent("session-xyz", "all_signed", null, AllSigned: true, "/path/signed.pdf");
        _eSignService.Setup(s => s.ParseWebhookEventAsync("payload")).ReturnsAsync(esignEvent);
        _leaseRepo.Setup(r => r.GetByExternalSigningSessionIdAsync("session-xyz")).ReturnsAsync(lease);
        _leaseRepo.Setup(r => r.UpdateAsync(It.IsAny<LeaseContract>()))
            .ReturnsAsync((LeaseContract l) => l);
        _eventRepo.Setup(r => r.AddAsync(It.IsAny<LeaseEvent>()))
            .ReturnsAsync((LeaseEvent e) => e);

        // Act
        await _sut.HandleESignEventAsync("payload");

        // Assert
        Assert.Equal(LeaseStatus.Signed, lease.Status);
        Assert.Equal("/path/signed.pdf", lease.SignedPdfStoragePath);
    }

    [Fact]
    public async Task CreateDraftAsync_WhenEndDateBeforeStartDate_ThrowsInvalidOperationException()
    {
        // Arrange
        var property = BuildProperty(hasApe: true);
        _propertyRepo.Setup(r => r.GetByIdAsync(PropertyId)).ReturnsAsync(property);

        var request = new CreateLeaseRequest(
            FiscalRegime: FiscalRegime.CedolareSecca,
            StartDate: new DateTime(2030, 9, 1, 0, 0, 0, DateTimeKind.Utc),
            EndDate: new DateTime(2026, 8, 31, 0, 0, 0, DateTimeKind.Utc), // before StartDate
            MonthlyRent: 1200.00m,
            Parties:
            [
                new CreatePartyRequest(PartyRole.Landlord, "Mario", "Rossi", "RSSMRA80A01H501Z", "IT", "mario@example.com"),
                new CreatePartyRequest(PartyRole.Tenant, "John", "Doe", "DOEJHN90B02Z123X", "IT", "john@example.com")
            ]);

        // Act & Assert
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _sut.CreateDraftAsync(PropertyId, OwnerId, request));
    }

    [Fact]
    public async Task CreateDraftAsync_WhenNoTenant_ThrowsInvalidOperationException()
    {
        // Arrange
        var property = BuildProperty(hasApe: true);
        _propertyRepo.Setup(r => r.GetByIdAsync(PropertyId)).ReturnsAsync(property);

        var request = new CreateLeaseRequest(
            FiscalRegime: FiscalRegime.CedolareSecca,
            StartDate: new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc),
            EndDate: new DateTime(2030, 8, 31, 0, 0, 0, DateTimeKind.Utc),
            MonthlyRent: 1200.00m,
            Parties: [new CreatePartyRequest(PartyRole.Landlord, "Mario", "Rossi", "RSSMRA80A01H501Z", "IT", "mario@example.com")]);

        // Act & Assert
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _sut.CreateDraftAsync(PropertyId, OwnerId, request));
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
            OrgId = Guid.NewGuid(),
            Status = status,
            FiscalRegime = FiscalRegime.CedolareSecca,
            StartDate = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc),
            EndDate = new DateTime(2030, 8, 31, 0, 0, 0, DateTimeKind.Utc),
            MonthlyRent = 1200m,
            Parties = []
        };
    }
}
