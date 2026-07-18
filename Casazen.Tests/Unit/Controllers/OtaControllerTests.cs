using System.Security.Claims;
using Casazen.Core.Entities;
using Casazen.Core.Services;
using Casazen.Web.BackgroundJobs;
using Casazen.Web.Controllers;
using Casazen.Web.DTOs;
using Hangfire;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Controllers;

public class OtaControllerTests
{
    private const string OwnerId = "auth0|owner_123";
    private const string AttackerId = "auth0|attacker_456";

    private readonly Mock<IOtaManager> _otaManager = new();
    private readonly Mock<IOtaIntegrationService> _otaIntegrationService = new();
    private readonly Mock<IPropertyService> _propertyService = new();
    private readonly Mock<IPropertyAuthorizationService> _authorizationService = new();
    private readonly Mock<IBackgroundJobClient> _backgroundJobClient = new();
    private readonly OtaController _controller;

    public OtaControllerTests()
    {
        _controller = new OtaController(
            _otaManager.Object,
            _otaIntegrationService.Object,
            _propertyService.Object,
            _authorizationService.Object,
            _backgroundJobClient.Object,
            Mock.Of<ILogger<OtaController>>());
    }

    [Fact]
    public async Task GetIntegrations_AsNonOwner_ReturnsForbiddenAndDoesNotLoadIntegrations()
    {
        var propertyId = Guid.NewGuid();
        SetUser(AttackerId);
        _propertyService.Setup(s => s.GetPropertyAsync(propertyId))
            .ReturnsAsync(new Property { Id = propertyId, OwnerId = OwnerId });

        var result = await _controller.GetIntegrations(propertyId);

        Assert.IsType<ForbidResult>(result);
        _otaIntegrationService.Verify(s => s.GetPropertyIntegrationsAsync(It.IsAny<Guid>()), Times.Never);
    }

    [Fact]
    public async Task GetIntegrations_AsOwner_ReturnsMaskedDtosWithoutRawSecrets()
    {
        var propertyId = Guid.NewGuid();
        var integration = new OtaIntegration
        {
            Id = Guid.NewGuid(),
            PropertyId = propertyId,
            Platform = "Airbnb",
            ExternalPropertyId = "external-123",
            ApiKey = "live_api_key_secret",
            ApiSecret = "live_api_secret",
            IsActive = true,
            LastSyncAt = DateTime.UtcNow,
            CreatedAt = DateTime.UtcNow.AddDays(-1)
        };

        SetUser(OwnerId);
        _propertyService.Setup(s => s.GetPropertyAsync(propertyId))
            .ReturnsAsync(new Property { Id = propertyId, OwnerId = OwnerId });
        _authorizationService.Setup(s => s.CanAccess(OwnerId, OwnerId, It.IsAny<IEnumerable<string>>()))
            .Returns(true);
        _otaIntegrationService.Setup(s => s.GetPropertyIntegrationsAsync(propertyId))
            .ReturnsAsync([integration]);
        _otaIntegrationService.Setup(s => s.MaskApiKey(integration.ApiKey))
            .Returns("live****cret");

        var result = await _controller.GetIntegrations(propertyId);

        var ok = Assert.IsType<OkObjectResult>(result);
        var dto = Assert.Single(Assert.IsAssignableFrom<IEnumerable<OtaIntegrationDto>>(ok.Value));
        Assert.Equal(integration.Id, dto.Id);
        Assert.Equal("live****cret", dto.ApiKeyMasked);
    }

    [Fact]
    public async Task UpdatePricing_AsNonOwner_ReturnsForbiddenAndDoesNotMutateProperty()
    {
        var propertyId = Guid.NewGuid();
        SetUser(AttackerId);
        _propertyService.Setup(s => s.GetPropertyAsync(propertyId))
            .ReturnsAsync(new Property { Id = propertyId, OwnerId = OwnerId });

        var result = await _controller.UpdatePricing(propertyId, 999m);

        Assert.IsType<ForbidResult>(result);
        _otaManager.Verify(m => m.UpdatePricingAsync(It.IsAny<Guid>(), It.IsAny<decimal>()), Times.Never);
    }

    private void SetUser(string userId)
    {
        var identity = new ClaimsIdentity([new Claim("sub", userId)], "TestAuth");
        _controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(identity) }
        };
    }
}
