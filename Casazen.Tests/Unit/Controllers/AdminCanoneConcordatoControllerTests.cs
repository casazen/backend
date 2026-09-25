using System.Security.Claims;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Services;
using Casazen.Web.Controllers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Controllers;

/// <summary>
/// Admin CRUD of the LTR regulatory reference data (LT-13, A7-22): the controller authorizes with
/// <c>AdminOnly</c> (checked by <see cref="Controller_EveryAction_IsAdminOnly"/>) and otherwise only shapes the
/// service's result into HTTP responses; the service's own behaviour is covered by
/// <c>RegulatoryReferenceDataAdminServiceTests</c>.
/// </summary>
public class AdminCanoneConcordatoControllerTests
{
    private const string AdminUserId = "auth0|admin";

    [Fact]
    public async Task GetAgreements_ReturnsOkWithServiceResult()
    {
        var admin = new Mock<IRegulatoryReferenceDataAdminService>();
        var rows = new List<AdminAgreementSummaryDto> { Summary() };
        admin.Setup(s => s.GetAgreementsAsync(It.IsAny<CancellationToken>())).ReturnsAsync(rows);
        var controller = CreateController(admin.Object);

        var result = await controller.GetAgreements(CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        Assert.Same(rows, ok.Value);
    }

    [Fact]
    public async Task GetAgreement_UnknownId_Returns404()
    {
        var admin = new Mock<IRegulatoryReferenceDataAdminService>();
        admin.Setup(s => s.GetAgreementAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((AdminAgreementDetailDto?)null);
        var controller = CreateController(admin.Object);

        var result = await controller.GetAgreement(Guid.NewGuid(), CancellationToken.None);

        var problem = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status404NotFound, problem.StatusCode);
    }

    [Fact]
    public async Task UpdateAgreement_Found_PassesTheCallerIdAndReturnsOk()
    {
        string? usedUserId = null;
        var admin = new Mock<IRegulatoryReferenceDataAdminService>();
        admin
            .Setup(s => s.UpdateAgreementAsync(It.IsAny<Guid>(), It.IsAny<UpdateAgreementInput>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback((Guid _, UpdateAgreementInput _, string userId, CancellationToken _) => usedUserId = userId)
            .ReturnsAsync(Detail());
        var controller = CreateController(admin.Object);

        var result = await controller.UpdateAgreement(Guid.NewGuid(), RulesInput(), CancellationToken.None);

        Assert.IsType<OkObjectResult>(result);
        Assert.Equal(AdminUserId, usedUserId);
    }

    [Fact]
    public async Task UpdateAgreement_NotFound_Returns404()
    {
        var admin = new Mock<IRegulatoryReferenceDataAdminService>();
        admin
            .Setup(s => s.UpdateAgreementAsync(It.IsAny<Guid>(), It.IsAny<UpdateAgreementInput>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((AdminAgreementDetailDto?)null);
        var controller = CreateController(admin.Object);

        var result = await controller.UpdateAgreement(Guid.NewGuid(), RulesInput(), CancellationToken.None);

        var problem = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status404NotFound, problem.StatusCode);
    }

    [Fact]
    public async Task UpdateBand_InvalidRange_LetsTheDomainRuleExceptionThrough()
    {
        // The API's error middleware turns it into 422 (see DomainException docs); the controller adds no handling of its own.
        var admin = new Mock<IRegulatoryReferenceDataAdminService>();
        admin
            .Setup(s => s.UpdateBandAsync(
                It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<UpdateRentBandInput>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new DomainRuleException(RegulatoryReferenceDataErrorCodes.InvalidBandRange, "LtrInvalidBandRange"));
        var controller = CreateController(admin.Object);
        var input = new UpdateRentBandInput("Unica", null, 50, 50, 20, 60, 20, 90, 20, 110);

        await Assert.ThrowsAsync<DomainRuleException>(
            () => controller.UpdateBand(Guid.NewGuid(), Guid.NewGuid(), input, CancellationToken.None));
    }

    [Fact]
    public async Task VerifyAgreement_Found_ReturnsOk()
    {
        var admin = new Mock<IRegulatoryReferenceDataAdminService>();
        admin
            .Setup(s => s.MarkAgreementVerifiedAsync(It.IsAny<Guid>(), It.IsAny<MarkVerifiedInput>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Detail());
        var controller = CreateController(admin.Object);
        var request = new MarkVerifiedRequest { VerifiedAt = new DateOnly(2026, 9, 1), Source = "Delibera" };

        var result = await controller.VerifyAgreement(Guid.NewGuid(), request, CancellationToken.None);

        Assert.IsType<OkObjectResult>(result);
    }

    [Fact]
    public async Task GetImuChannels_ReturnsOkWithServiceResult()
    {
        var admin = new Mock<IRegulatoryReferenceDataAdminService>();
        var rows = new List<AdminImuChannelDto> { Channel() };
        admin.Setup(s => s.GetImuChannelsAsync(It.IsAny<CancellationToken>())).ReturnsAsync(rows);
        var controller = CreateController(admin.Object);

        var result = await controller.GetImuChannels(CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        Assert.Same(rows, ok.Value);
    }

    [Fact]
    public async Task UpdateImuChannel_NotFound_Returns404()
    {
        var admin = new Mock<IRegulatoryReferenceDataAdminService>();
        admin
            .Setup(s => s.UpdateImuChannelAsync(It.IsAny<Guid>(), It.IsAny<UpdateImuChannelInput>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((AdminImuChannelDto?)null);
        var controller = CreateController(admin.Object);

        var result = await controller.UpdateImuChannel(Guid.NewGuid(), ChannelInput(), CancellationToken.None);

        var problem = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status404NotFound, problem.StatusCode);
    }

    [Fact]
    public async Task GetAudit_PassesEntityIdAndReturnsOk()
    {
        Guid? usedId = null;
        var entries = new List<RegulatoryAuditEntryDto>();
        var admin = new Mock<IRegulatoryReferenceDataAdminService>();
        admin
            .Setup(s => s.GetAuditAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .Callback((Guid id, CancellationToken _) => usedId = id)
            .ReturnsAsync(entries);
        var controller = CreateController(admin.Object);
        var entityId = Guid.NewGuid();

        var result = await controller.GetAudit(entityId, CancellationToken.None);

        Assert.IsType<OkObjectResult>(result);
        Assert.Equal(entityId, usedId);
    }

    [Theory]
    [InlineData(nameof(AdminCanoneConcordatoController.GetAgreements))]
    [InlineData(nameof(AdminCanoneConcordatoController.GetAgreement))]
    [InlineData(nameof(AdminCanoneConcordatoController.UpdateAgreement))]
    [InlineData(nameof(AdminCanoneConcordatoController.UpdateBand))]
    [InlineData(nameof(AdminCanoneConcordatoController.VerifyAgreement))]
    [InlineData(nameof(AdminCanoneConcordatoController.GetImuChannels))]
    [InlineData(nameof(AdminCanoneConcordatoController.UpdateImuChannel))]
    [InlineData(nameof(AdminCanoneConcordatoController.VerifyImuChannel))]
    [InlineData(nameof(AdminCanoneConcordatoController.GetAudit))]
    public void Controller_EveryAction_IsAdminOnly(string actionName)
    {
        // Global reference data, not tenant-owned (LT-13): platform admins only, no per-row ownership check needed.
        var classPolicies = typeof(AdminCanoneConcordatoController)
            .GetCustomAttributes(typeof(Microsoft.AspNetCore.Authorization.AuthorizeAttribute), false)
            .Cast<Microsoft.AspNetCore.Authorization.AuthorizeAttribute>()
            .Select(a => a.Policy);
        Assert.Contains("AdminOnly", classPolicies);
        // No method-level [Authorize] narrows or replaces it for any of these actions.
        var method = typeof(AdminCanoneConcordatoController).GetMethods()
            .First(m => m.Name == actionName && m.DeclaringType == typeof(AdminCanoneConcordatoController));
        Assert.Empty(method.GetCustomAttributes(typeof(Microsoft.AspNetCore.Authorization.AuthorizeAttribute), false));
    }

    private static AdminCanoneConcordatoController CreateController(IRegulatoryReferenceDataAdminService admin)
    {
        var controller = new AdminCanoneConcordatoController(admin)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", AdminUserId)], "test")),
                    RequestServices = new ServiceCollection().AddLogging().AddLocalization().BuildServiceProvider(),
                },
            },
        };
        return controller;
    }

    private static AdminAgreementSummaryDto Summary() => new(
        Guid.NewGuid(), "Seveso", "Lombardia", "Accordo locale Quadro", DataCompleteness.Partial,
        4, ["Unica"], "https://example.org", null, null, null, true, null);

    private static AdminAgreementDetailDto Detail() => new(
        Summary(),
        new AgreementRulesDto(2, 3, 3, 2, "D1,D2,D4,D6,D7,D9", 4, 4, CoefficientCombination.Additive,
            15, 5, 40, 20, 50, 60, 10, 120, 20, 50, 30, 25, 10, 3, 5, 6),
        new DateTime(2024, 3, 15, 0, 0, 0, DateTimeKind.Utc),
        new DateTime(2024, 5, 1, 0, 0, 0, DateTimeKind.Utc),
        null, [], []);

    private static UpdateAgreementInput RulesInput() => new(
        DataCompleteness: DataCompleteness.Partial, SourceUrl: null, ExpiresAt: null, ExpiryNote: null,
        RemainsInForceUntilReplaced: true, RequiredTypeACount: 2, SubFascia2MinTypeBCount: 3,
        SubFascia3MinTypeCCount: 3, SubFascia3MinQualifyingTypeDCount: 2, SubFascia3QualifyingTypeDElements: null,
        SubFascia3MaxMinTypeDCount: 4, StoveHeatingMinTypeBCount: 4, CoefficientCombination: CoefficientCombination.Additive,
        FurnishedUpliftPercent: 15, AirConditioningUpliftPercent: 5, SmallSqmMax: 40, SmallSqmUpliftPercent: 20,
        MidSqmMin: 50, MidSqmMax: 60, MidSqmUpliftPercent: 10, LargeSqmMin: 120, LargeSqmReductionPercent: 20,
        GarageAppurtenancePercent: 50, BalconyAppurtenancePercent: 30, OtherAppurtenancePercent: 25,
        GreenAreaAppurtenancePercent: 10, Duration4UpliftPercent: 3, Duration5UpliftPercent: 5, Duration6UpliftPercent: 6);

    private static AdminImuChannelDto Channel() => new(
        Guid.NewGuid(), "Seveso", "Lombardia", "Ufficio Tributi", null, null, null, null,
        null, null, null, null, null, null, null, DataCompleteness.Partial, null, null, null);

    private static UpdateImuChannelInput ChannelInput() => new(
        RecipientOffice: "Ufficio Tributi", Email: null, Pec: null, PostalAddress: null, Instructions: null,
        RatePercent: null, EffectiveRatePercent: null, RateYear: null, RateKind: null, RateNotes: null,
        RateSourceUrl: null, SourceUrl: null, DataCompleteness: DataCompleteness.Partial);
}
