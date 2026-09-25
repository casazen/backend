using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Leases;
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
    private readonly Mock<ILeaseEventRepository> _eventRepo = new();
    private readonly Mock<IPropertyRepository> _propertyRepo = new();
    private readonly Mock<IApeComplianceService> _apeCompliance = new();
    private readonly Mock<ICanoneConcordatoEligibilityService> _canoneEligibility = new();
    private readonly LeaseWorkflowService _sut;

    private static readonly string OwnerId = "auth0|owner123";
    private static readonly Guid PropertyId = Guid.NewGuid();

    public LeaseWorkflowServiceTests()
    {
        _apeCompliance.Setup(s => s.EnsurePropertyHasValidApeAsync(It.IsAny<Guid>()))
            .Returns(Task.CompletedTask);
        _sut = new LeaseWorkflowService(
            _leaseRepo.Object,
            _eventRepo.Object,
            _propertyRepo.Object,
            _apeCompliance.Object,
            _canoneEligibility.Object,
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
        var result = await _sut.CreateDraftAsync(PropertyId, request);

        // Assert
        Assert.Equal(LeaseStatus.Draft, result.Status);
        Assert.Equal(request.MonthlyRent, result.MonthlyRent);
        // LT-04 (A7-04): no stipula yet, so no deadline stored (it was StartDate + 30).
        Assert.Null(result.StipulaDate);
        Assert.Null(result.RegistrationDeadline);
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
            _sut.CreateDraftAsync(PropertyId, BuildCreateRequest()));
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
            _sut.CreateDraftAsync(PropertyId, BuildCreateRequest()));
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

        var result = await _sut.CreateDraftAsync(PropertyId, BuildCreateRequest());

        Assert.Equal(LeaseStatus.Draft, result.Status);
        _apeCompliance.Verify(s => s.EnsurePropertyHasValidApeAsync(PropertyId), Times.Once);
    }

    [Fact]
    public async Task CreateDraftAsync_PropertyOwnedByAnotherOrgMember_CreatesDraftInThePropertyOrg()
    {
        // The caller (owner, or an org-wide member such as a PropertyManager) is authorized on the property by the
        // controller (TN-3): the service no longer rejects a caller who is not the owner (LT-05).
        var property = BuildProperty(hasApe: true, ownerId: "auth0|different-owner");
        property.OrgId = Guid.NewGuid();
        _propertyRepo.Setup(r => r.GetByIdAsync(PropertyId)).ReturnsAsync(property);
        _leaseRepo.Setup(r => r.AddAsync(It.IsAny<LeaseContract>()))
            .ReturnsAsync((LeaseContract l) => l);
        _eventRepo.Setup(r => r.AddAsync(It.IsAny<LeaseEvent>()))
            .ReturnsAsync((LeaseEvent e) => e);

        var result = await _sut.CreateDraftAsync(PropertyId, BuildCreateRequest());

        Assert.Equal(LeaseStatus.Draft, result.Status);
        Assert.Equal(property.OrgId, result.OrgId);
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
        var result = await _sut.CreateDraftAsync(PropertyId, request);

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
        var result = await _sut.CreateDraftAsync(PropertyId, request);

        // Assert
        var tenant = result.Parties.Single(p => p.Role == PartyRole.Tenant);
        Assert.False(tenant.IsExtraEU);
        Assert.False(result.HasExtraEUTenant);
    }

    [Fact]
    public async Task CreateDraftAsync_LowerCaseEuCitizenship_StoredUpperCaseAndNotExtraEu()
    {
        // LT-07: the EU list (EuMemberStates) is compared on the normalized code, which is also what is stored.
        _propertyRepo.Setup(r => r.GetByIdAsync(PropertyId)).ReturnsAsync(BuildProperty(hasApe: true));
        _leaseRepo.Setup(r => r.AddAsync(It.IsAny<LeaseContract>())).ReturnsAsync((LeaseContract l) => l);
        _eventRepo.Setup(r => r.AddAsync(It.IsAny<LeaseEvent>())).ReturnsAsync((LeaseEvent e) => e);

        var result = await _sut.CreateDraftAsync(PropertyId, BuildCreateRequest(tenantCitizenship: "de"));

        var tenant = result.Parties.Single(p => p.Role == PartyRole.Tenant);
        Assert.Equal("DE", tenant.Citizenship);
        Assert.False(tenant.IsExtraEU);
    }

    [Fact]
    public async Task GetLeaseDetailAsync_ExistingLease_ReturnsItForTheCallerToAuthorize()
    {
        // Arrange: ownership is no longer decided here but by the controller (TN-3 HostResource check).
        var lease = BuildLease(LeaseStatus.Draft);
        _leaseRepo.Setup(r => r.GetByIdWithDetailsAsync(lease.Id)).ReturnsAsync(lease);

        // Act
        var result = await _sut.GetLeaseDetailAsync(lease.Id);

        // Assert
        Assert.Same(lease, result);
    }

    [Fact]
    public async Task GetLeaseDetailAsync_UnknownLease_ReturnsNull()
    {
        // Arrange
        _leaseRepo.Setup(r => r.GetByIdWithDetailsAsync(It.IsAny<Guid>())).ReturnsAsync((LeaseContract?)null);

        // Act
        var result = await _sut.GetLeaseDetailAsync(Guid.NewGuid());

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public async Task CreateDraftAsync_WhenEndDateBeforeStartDate_Throws422EndBeforeStart()
    {
        // Arrange
        var property = BuildProperty(hasApe: true);
        _propertyRepo.Setup(r => r.GetByIdAsync(PropertyId)).ReturnsAsync(property);

        var request = BuildCreateRequest() with
        {
            StartDate = new DateTime(2030, 9, 1, 0, 0, 0, DateTimeKind.Utc),
            EndDate = new DateTime(2026, 8, 31, 0, 0, 0, DateTimeKind.Utc), // before StartDate
        };

        // Act & Assert
        var ex = await Assert.ThrowsAsync<DomainRuleException>(() => _sut.CreateDraftAsync(PropertyId, request));
        Assert.Equal(LeaseTermErrorCodes.EndBeforeStart, ex.Code);
    }

    [Theory]
    // LT-10 (A7-13): libero 4+4 at least 4 years, concordato 3+2 at least 3 years, transitorio 1-18 months.
    [InlineData(LeaseContractType.Libero, "2026-09-01", "2030-08-30", "lease_term_too_short")]
    [InlineData(LeaseContractType.Libero, "2026-09-01", "2027-08-31", "lease_term_too_short")]
    [InlineData(LeaseContractType.Concordato, "2026-09-01", "2027-08-31", "lease_term_too_short")]
    [InlineData(LeaseContractType.Concordato, "2026-09-01", "2029-08-30", "lease_term_too_short")]
    [InlineData(LeaseContractType.Transitorio, "2026-09-01", "2026-09-20", "lease_term_too_short")]
    [InlineData(LeaseContractType.Transitorio, "2026-09-01", "2028-03-01", "lease_term_too_long")]
    public async Task CreateDraftAsync_TermNotAllowedForContractType_Throws422(
        LeaseContractType contractType, string start, string end, string expectedCode)
    {
        _propertyRepo.Setup(r => r.GetByIdAsync(PropertyId)).ReturnsAsync(BuildProperty(hasApe: true));
        var request = BuildCreateRequest(monthlyRent: 400m, canoneConcordatoCharacteristics: Characteristics()) with
        {
            FiscalRegime = null,
            ContractType = contractType,
            TaxRegime = LeaseTaxRegime.CedolareSecca,
            StartDate = DateTime.Parse(start, System.Globalization.CultureInfo.InvariantCulture),
            EndDate = DateTime.Parse(end, System.Globalization.CultureInfo.InvariantCulture),
        };

        var ex = await Assert.ThrowsAsync<DomainRuleException>(() => _sut.CreateDraftAsync(PropertyId, request));

        Assert.Equal(expectedCode, ex.Code);
        _leaseRepo.Verify(r => r.AddAsync(It.IsAny<LeaseContract>()), Times.Never);
        _canoneEligibility.Verify(
            s => s.CalculateAsync(It.IsAny<Guid>(), It.IsAny<RentBandCharacteristics>(), It.IsAny<LeaseTerm>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Theory]
    [InlineData(LeaseContractType.Libero, "2026-09-01", "2030-08-31")]
    [InlineData(LeaseContractType.Transitorio, "2026-09-01", "2026-09-30")]
    [InlineData(LeaseContractType.Transitorio, "2026-09-01", "2028-02-29")]
    public async Task CreateDraftAsync_TermAllowedForContractType_PersistsTypeTaxRegimeAndLegacyValue(
        LeaseContractType contractType, string start, string end)
    {
        ArrangeCreation();
        var request = BuildCreateRequest() with
        {
            FiscalRegime = null,
            ContractType = contractType,
            TaxRegime = LeaseTaxRegime.Ordinario,
            SecurityDeposit = 2400m,
            StartDate = DateTime.Parse(start, System.Globalization.CultureInfo.InvariantCulture),
            EndDate = DateTime.Parse(end, System.Globalization.CultureInfo.InvariantCulture),
        };

        var result = await _sut.CreateDraftAsync(PropertyId, request);

        Assert.Equal(contractType, result.ContractType);
        Assert.Equal(LeaseTaxRegime.Ordinario, result.TaxRegime);
        Assert.Equal(FiscalRegime.RegimeOrdinario, result.FiscalRegime);
        Assert.Equal(2400m, result.SecurityDeposit);
        Assert.Null(result.ConcordatoAssessment);
    }

    [Fact]
    public async Task CreateDraftAsync_ContractTypeWithoutTaxRegime_Throws422TaxRegimeRequired()
    {
        _propertyRepo.Setup(r => r.GetByIdAsync(PropertyId)).ReturnsAsync(BuildProperty(hasApe: true));
        var request = BuildCreateRequest() with { FiscalRegime = null, ContractType = LeaseContractType.Libero };

        var ex = await Assert.ThrowsAsync<DomainRuleException>(() => _sut.CreateDraftAsync(PropertyId, request));

        Assert.Equal(LeaseTermErrorCodes.TaxRegimeRequired, ex.Code);
    }

    [Fact]
    public async Task CreateDraftAsync_LegacyCanoneConcordato_MapsToConcordatoWithUnknownTaxRegime()
    {
        ArrangeCreation();
        ArrangeRange(DataCompleteness.Partial);
        var request = BuildCreateRequest(
            fiscalRegime: FiscalRegime.CanoneConcordato, monthlyRent: 400m, canoneConcordatoCharacteristics: Characteristics());

        var result = await _sut.CreateDraftAsync(PropertyId, request);

        Assert.Equal(LeaseContractType.Concordato, result.ContractType);
        Assert.Null(result.TaxRegime);
        Assert.Equal(FiscalRegime.CanoneConcordato, result.FiscalRegime);
    }

    [Fact]
    public async Task CreateDraftAsync_ConcordatoUnderOrdinaryRegime_KeepsBothTypeAndRegime()
    {
        // A7-13: "concordato + regime ordinario" could not be represented with the combined value.
        ArrangeCreation();
        ArrangeRange(DataCompleteness.Complete);
        var request = ConcordatoRequest(400m) with { TaxRegime = LeaseTaxRegime.Ordinario };

        var result = await _sut.CreateDraftAsync(PropertyId, request);

        Assert.Equal(LeaseContractType.Concordato, result.ContractType);
        Assert.Equal(LeaseTaxRegime.Ordinario, result.TaxRegime);
        Assert.Equal(FiscalRegime.CanoneConcordato, result.FiscalRegime);
    }

    [Fact]
    public async Task CreateDraftAsync_ConcordatoWithoutCharacteristics_Throws422CharacteristicsRequired()
    {
        _propertyRepo.Setup(r => r.GetByIdAsync(PropertyId)).ReturnsAsync(BuildProperty(hasApe: true));
        var request = ConcordatoRequest(400m) with { CanoneConcordatoCharacteristics = null };

        var ex = await Assert.ThrowsAsync<DomainRuleException>(() => _sut.CreateDraftAsync(PropertyId, request));

        Assert.Equal(ConcordatoErrorCodes.CharacteristicsRequired, ex.Code);
        _leaseRepo.Verify(r => r.AddAsync(It.IsAny<LeaseContract>()), Times.Never);
    }

    [Fact]
    public async Task CreateDraftAsync_ConcordatoRange_UsesTheTermOfTheLeaseDates()
    {
        // A7-12: the range is computed with the real term (5 years here), never with a client-side year count.
        ArrangeCreation();
        LeaseTerm? usedTerm = null;
        _canoneEligibility
            .Setup(s => s.CalculateAsync(PropertyId, It.IsAny<RentBandCharacteristics>(), It.IsAny<LeaseTerm>(), It.IsAny<CancellationToken>()))
            .Callback((Guid _, RentBandCharacteristics _, LeaseTerm term, CancellationToken _) => usedTerm = term)
            .ReturnsAsync(Range(DataCompleteness.Complete));
        var request = ConcordatoRequest(400m) with { EndDate = new DateTime(2031, 8, 31, 0, 0, 0, DateTimeKind.Utc) };

        await _sut.CreateDraftAsync(PropertyId, request);

        Assert.Equal(new LeaseTerm(60, 0), usedTerm);
    }

    [Fact]
    public async Task CreateDraftAsync_ConcordatoOutsideRangeWithVerifiedData_Throws422AndDoesNotSave()
    {
        _propertyRepo.Setup(r => r.GetByIdAsync(PropertyId)).ReturnsAsync(BuildProperty(hasApe: true));
        ArrangeRange(DataCompleteness.Complete);

        var ex = await Assert.ThrowsAsync<DomainRuleException>(() => _sut.CreateDraftAsync(PropertyId, ConcordatoRequest(900m)));

        Assert.Equal(ConcordatoErrorCodes.RentOutOfRange, ex.Code);
        Assert.Equal(new object[] { 108.34m, 460.41m }, ex.MessageArgs);
        _leaseRepo.Verify(r => r.AddAsync(It.IsAny<LeaseContract>()), Times.Never);
    }

    [Fact]
    public async Task CreateDraftAsync_ConcordatoOutsideIndicativeRangeWithPartialData_CreatesLeaseWithWarning()
    {
        // A7-23: with data not confirmed by a lawyer or a signatory organization the range is a guide, not a block.
        ArrangeCreation();
        ArrangeRange(DataCompleteness.Partial);

        var result = await _sut.CreateDraftAsync(PropertyId, ConcordatoRequest(900m));

        var assessment = Assert.IsType<LeaseConcordatoAssessment>(result.ConcordatoAssessment);
        Assert.True(assessment.Indicative);
        Assert.False(assessment.RentWithinRange);
        Assert.Equal(DataCompleteness.Partial, assessment.DataCompleteness);
        _leaseRepo.Verify(r => r.AddAsync(It.IsAny<LeaseContract>()), Times.Once);
    }

    [Fact]
    public async Task CreateDraftAsync_ConcordatoWithinRange_StoresCharacteristicsAndRange()
    {
        ArrangeCreation();
        ArrangeRange(DataCompleteness.Complete);
        var characteristics = Characteristics() with { GarageSqm = 12m, StoveHeating = true, CadastralSheet = " 7 " };

        var result = await _sut.CreateDraftAsync(PropertyId, ConcordatoRequest(400m) with
        {
            CanoneConcordatoCharacteristics = characteristics,
        });

        var assessment = Assert.IsType<LeaseConcordatoAssessment>(result.ConcordatoAssessment);
        Assert.Equal(65m, assessment.Sqm);
        Assert.Equal(12m, assessment.GarageSqm);
        Assert.True(assessment.StoveHeating);
        Assert.Equal("7", assessment.CadastralSheet);
        Assert.Equal("Unica", assessment.Zone);
        Assert.Equal(2, assessment.SubFascia);
        Assert.Equal(3, assessment.ContractYears);
        Assert.Equal(1300m, assessment.CanoneMinAnnuo);
        Assert.Equal(5525m, assessment.CanoneMaxAnnuo);
        Assert.True(assessment.RentWithinRange);
        Assert.False(assessment.Indicative);
    }

    [Theory]
    [InlineData(CanoneConcordatoReasonCodes.DataUnavailable, ConcordatoErrorCodes.RangeUnavailable)]
    [InlineData(CanoneConcordatoReasonCodes.ZoneRequired, ConcordatoErrorCodes.ZoneRequired)]
    [InlineData(CanoneConcordatoReasonCodes.SurfaceOutOfBands, ConcordatoErrorCodes.SurfaceOutOfBands)]
    public async Task CreateDraftAsync_ConcordatoRangeNotAvailable_Throws422WithTheReason(string reasonCode, string expectedCode)
    {
        _propertyRepo.Setup(r => r.GetByIdAsync(PropertyId)).ReturnsAsync(BuildProperty(hasApe: true));
        _canoneEligibility
            .Setup(s => s.CalculateAsync(PropertyId, It.IsAny<RentBandCharacteristics>(), It.IsAny<LeaseTerm>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CanoneConcordatoEligibilityDto(
                false, "reason", "Seveso", null, null, null, null, null, null,
                DataCompleteness.Partial, false, false, true, CanoneConcordatoCopy.Disclaimer)
            {
                ReasonCode = reasonCode,
            });

        var ex = await Assert.ThrowsAsync<DomainRuleException>(() => _sut.CreateDraftAsync(PropertyId, ConcordatoRequest(400m)));

        Assert.Equal(expectedCode, ex.Code);
        _leaseRepo.Verify(r => r.AddAsync(It.IsAny<LeaseContract>()), Times.Never);
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
            _sut.CreateDraftAsync(PropertyId, request));
    }

    private void ArrangeCreation()
    {
        _propertyRepo.Setup(r => r.GetByIdAsync(PropertyId)).ReturnsAsync(BuildProperty(hasApe: true));
        _leaseRepo.Setup(r => r.AddAsync(It.IsAny<LeaseContract>())).ReturnsAsync((LeaseContract l) => l);
        _eventRepo.Setup(r => r.AddAsync(It.IsAny<LeaseEvent>())).ReturnsAsync((LeaseEvent e) => e);
    }

    private void ArrangeRange(DataCompleteness completeness) =>
        _canoneEligibility
            .Setup(s => s.CalculateAsync(PropertyId, It.IsAny<RentBandCharacteristics>(), It.IsAny<LeaseTerm>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Range(completeness));

    /// <summary>Seveso, 65 mq, sub-fascia 2, 3 years: 1.300,00-5.525,00 euro a year.</summary>
    private static CanoneConcordatoEligibilityDto Range(DataCompleteness completeness) =>
        new(true, null, "Seveso", "Unica", 2, 1300m, 5525m, 108.34m, 460.41m,
            completeness, true, false, true, CanoneConcordatoCopy.Disclaimer)
        {
            ContractYears = 3,
            UsableSqm = 65m,
            Indicative = completeness != DataCompleteness.Complete,
        };

    private static RentBandCharacteristics Characteristics() => new()
    {
        Sqm = 65m,
        TypeAElementCount = 2,
        TypeBElementCount = 3,
        ZoneName = "Unica",
    };

    private static CreateLeaseRequest ConcordatoRequest(decimal monthlyRent) =>
        BuildCreateRequest(monthlyRent: monthlyRent, canoneConcordatoCharacteristics: Characteristics()) with
        {
            FiscalRegime = null,
            ContractType = LeaseContractType.Concordato,
            TaxRegime = LeaseTaxRegime.CedolareSecca,
            StartDate = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc),
            EndDate = new DateTime(2029, 8, 31, 0, 0, 0, DateTimeKind.Utc),
        };

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

    private static CreateLeaseRequest BuildCreateRequest(
        string tenantCitizenship = "IT",
        FiscalRegime fiscalRegime = FiscalRegime.CedolareSecca,
        decimal monthlyRent = 1200.00m,
        RentBandCharacteristics? canoneConcordatoCharacteristics = null) => new(
        FiscalRegime: fiscalRegime,
        StartDate: new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc),
        EndDate: new DateTime(2030, 8, 31, 0, 0, 0, DateTimeKind.Utc),
        MonthlyRent: monthlyRent,
        Parties:
        [
            new CreatePartyRequest(PartyRole.Landlord, "Mario", "Rossi", "RSSMRA80A01H501Z", "IT", "mario@example.com"),
            new CreatePartyRequest(PartyRole.Tenant, "John", "Doe", "DOEJHN90B02Z123X", tenantCitizenship, "john@example.com")
        ],
        CanoneConcordatoCharacteristics: canoneConcordatoCharacteristics);

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
            SignedPdfStoragePath = status == LeaseStatus.Signed ? "/path/signed.pdf" : null,
            FiscalRegime = FiscalRegime.CedolareSecca,
            StartDate = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc),
            EndDate = new DateTime(2030, 8, 31, 0, 0, 0, DateTimeKind.Utc),
            MonthlyRent = 1200m,
            Parties = []
        };
    }
}
