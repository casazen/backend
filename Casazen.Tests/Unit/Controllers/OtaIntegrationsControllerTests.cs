using System.Security.Claims;
using Casazen.Core.Entities;
using Casazen.Core.Services;
using Casazen.Web.Controllers;
using Casazen.Web.DTOs;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Controllers;

public class OtaIntegrationsControllerTests
{
    private const string OwnerId = "auth0|owner_123";
    private const string AttackerId = "auth0|attacker_456";

    private readonly Mock<IOtaIntegrationService> _mockOtaService;
    private readonly Mock<IPropertyService> _mockPropertyService;
    private readonly Mock<IPropertyAuthorizationService> _mockAuthz;
    private readonly Mock<ILogger<OtaIntegrationsController>> _mockLogger;
    private readonly OtaIntegrationsController _controller;

    public OtaIntegrationsControllerTests()
    {
        _mockOtaService = new Mock<IOtaIntegrationService>();
        _mockPropertyService = new Mock<IPropertyService>();
        _mockAuthz = new Mock<IPropertyAuthorizationService>();
        _mockLogger = new Mock<ILogger<OtaIntegrationsController>>();
        _controller = new OtaIntegrationsController(
            _mockOtaService.Object,
            _mockPropertyService.Object,
            _mockAuthz.Object,
            _mockLogger.Object);
    }

    private void SetUser(string userId)
    {
        var claims = new List<Claim> { new("sub", userId) };
        var identity = new ClaimsIdentity(claims, "TestAuth");
        _controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(identity) }
        };
    }

    private void AllowAuthorization() =>
        _mockAuthz.Setup(x => x.CanAccess(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<IEnumerable<string>>()))
            .Returns(true);

    private Property MakeProperty(Guid id) => new() { Id = id, OwnerId = OwnerId };

    private OtaIntegration MakeIntegration(Guid propertyId) => new()
    {
        Id = Guid.NewGuid(),
        PropertyId = propertyId,
        Platform = "Airbnb",
        ExternalPropertyId = "ext-123",
        ApiKey = "secret",
        IsActive = true,
        CreatedAt = DateTime.UtcNow
    };

    // ─── GetAll ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetAll_AsOwner_ReturnsOk()
    {
        var propertyId = Guid.NewGuid();
        SetUser(OwnerId);
        AllowAuthorization();
        _mockPropertyService.Setup(x => x.GetPropertyAsync(propertyId)).ReturnsAsync(MakeProperty(propertyId));
        _mockOtaService.Setup(x => x.GetPropertyIntegrationsAsync(propertyId))
            .ReturnsAsync([MakeIntegration(propertyId)]);
        _mockOtaService.Setup(x => x.MaskApiKey(It.IsAny<string>())).Returns("****");

        var result = await _controller.GetAll(propertyId);

        Assert.IsType<OkObjectResult>(result.Result);
    }

    [Fact]
    public async Task GetAll_AsNonOwner_ReturnsForbidden()
    {
        var propertyId = Guid.NewGuid();
        SetUser(AttackerId);
        _mockPropertyService.Setup(x => x.GetPropertyAsync(propertyId)).ReturnsAsync(MakeProperty(propertyId));

        var result = await _controller.GetAll(propertyId);

        Assert.IsType<ForbidResult>(result.Result);
    }

    [Fact]
    public async Task GetAll_PropertyNotFound_ReturnsNotFound()
    {
        var propertyId = Guid.NewGuid();
        SetUser(OwnerId);
        _mockPropertyService.Setup(x => x.GetPropertyAsync(propertyId)).ReturnsAsync((Property?)null);

        var result = await _controller.GetAll(propertyId);

        Assert.IsType<NotFoundResult>(result.Result);
    }

    // ─── Get ─────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Get_AsOwner_ReturnsOk()
    {
        var propertyId = Guid.NewGuid();
        var integration = MakeIntegration(propertyId);
        SetUser(OwnerId);
        AllowAuthorization();
        _mockPropertyService.Setup(x => x.GetPropertyAsync(propertyId)).ReturnsAsync(MakeProperty(propertyId));
        _mockOtaService.Setup(x => x.GetIntegrationAsync(integration.Id)).ReturnsAsync(integration);
        _mockOtaService.Setup(x => x.MaskApiKey(It.IsAny<string>())).Returns("****");

        var result = await _controller.Get(propertyId, integration.Id);

        Assert.IsType<OkObjectResult>(result.Result);
    }

    [Fact]
    public async Task Get_AsNonOwner_ReturnsForbidden()
    {
        var propertyId = Guid.NewGuid();
        SetUser(AttackerId);
        _mockPropertyService.Setup(x => x.GetPropertyAsync(propertyId)).ReturnsAsync(MakeProperty(propertyId));

        var result = await _controller.Get(propertyId, Guid.NewGuid());

        Assert.IsType<ForbidResult>(result.Result);
    }

    [Fact]
    public async Task Get_IntegrationBelongsToDifferentProperty_ReturnsNotFound()
    {
        var propertyId = Guid.NewGuid();
        var otherPropertyId = Guid.NewGuid();
        var integration = MakeIntegration(otherPropertyId);
        SetUser(OwnerId);
        AllowAuthorization();
        _mockPropertyService.Setup(x => x.GetPropertyAsync(propertyId)).ReturnsAsync(MakeProperty(propertyId));
        _mockOtaService.Setup(x => x.GetIntegrationAsync(integration.Id)).ReturnsAsync(integration);

        var result = await _controller.Get(propertyId, integration.Id);

        Assert.IsType<NotFoundResult>(result.Result);
    }

    // ─── Create ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Create_AsOwner_ReturnsCreated()
    {
        var propertyId = Guid.NewGuid();
        var integration = MakeIntegration(propertyId);
        SetUser(OwnerId);
        AllowAuthorization();
        _mockPropertyService.Setup(x => x.GetPropertyAsync(propertyId)).ReturnsAsync(MakeProperty(propertyId));
        _mockOtaService.Setup(x => x.CreateIntegrationAsync(propertyId, "Airbnb", "ext-123", "key"))
            .ReturnsAsync(integration);
        _mockOtaService.Setup(x => x.MaskApiKey(It.IsAny<string>())).Returns("****");

        var request = new CreateOtaIntegrationRequest
        {
            PropertyId = propertyId,
            Platform = "Airbnb",
            ExternalPropertyId = "ext-123",
            ApiKey = "key"
        };

        var result = await _controller.Create(propertyId, request);

        Assert.IsType<CreatedAtActionResult>(result.Result);
    }

    [Fact]
    public async Task Create_AsNonOwner_ReturnsForbidden()
    {
        var propertyId = Guid.NewGuid();
        SetUser(AttackerId);
        _mockPropertyService.Setup(x => x.GetPropertyAsync(propertyId)).ReturnsAsync(MakeProperty(propertyId));

        var request = new CreateOtaIntegrationRequest { PropertyId = propertyId };
        var result = await _controller.Create(propertyId, request);

        Assert.IsType<ForbidResult>(result.Result);
    }

    [Fact]
    public async Task Create_PropertyIdMismatch_ReturnsBadRequest()
    {
        var propertyId = Guid.NewGuid();
        SetUser(OwnerId);
        AllowAuthorization();
        _mockPropertyService.Setup(x => x.GetPropertyAsync(propertyId)).ReturnsAsync(MakeProperty(propertyId));

        var request = new CreateOtaIntegrationRequest { PropertyId = Guid.NewGuid() };
        var result = await _controller.Create(propertyId, request);

        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    // ─── Update ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Update_AsOwner_ReturnsNoContent()
    {
        var propertyId = Guid.NewGuid();
        var integration = MakeIntegration(propertyId);
        SetUser(OwnerId);
        AllowAuthorization();
        _mockPropertyService.Setup(x => x.GetPropertyAsync(propertyId)).ReturnsAsync(MakeProperty(propertyId));
        _mockOtaService.Setup(x => x.GetIntegrationAsync(integration.Id)).ReturnsAsync(integration);
        _mockOtaService.Setup(x => x.UpdateIntegrationAsync(integration.Id, null, null, null)).Returns(Task.CompletedTask);

        var result = await _controller.Update(propertyId, integration.Id, new UpdateOtaIntegrationRequest());

        Assert.IsType<NoContentResult>(result);
    }

    [Fact]
    public async Task Update_AsNonOwner_ReturnsForbidden()
    {
        var propertyId = Guid.NewGuid();
        SetUser(AttackerId);
        _mockPropertyService.Setup(x => x.GetPropertyAsync(propertyId)).ReturnsAsync(MakeProperty(propertyId));

        var result = await _controller.Update(propertyId, Guid.NewGuid(), new UpdateOtaIntegrationRequest());

        Assert.IsType<ForbidResult>(result);
    }

    // ─── Delete ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Delete_AsOwner_ReturnsNoContent()
    {
        var propertyId = Guid.NewGuid();
        var integration = MakeIntegration(propertyId);
        SetUser(OwnerId);
        AllowAuthorization();
        _mockPropertyService.Setup(x => x.GetPropertyAsync(propertyId)).ReturnsAsync(MakeProperty(propertyId));
        _mockOtaService.Setup(x => x.GetIntegrationAsync(integration.Id)).ReturnsAsync(integration);
        _mockOtaService.Setup(x => x.DeleteIntegrationAsync(integration.Id)).Returns(Task.CompletedTask);

        var result = await _controller.Delete(propertyId, integration.Id);

        Assert.IsType<NoContentResult>(result);
    }

    [Fact]
    public async Task Delete_AsNonOwner_ReturnsForbidden()
    {
        var propertyId = Guid.NewGuid();
        SetUser(AttackerId);
        _mockPropertyService.Setup(x => x.GetPropertyAsync(propertyId)).ReturnsAsync(MakeProperty(propertyId));

        var result = await _controller.Delete(propertyId, Guid.NewGuid());

        Assert.IsType<ForbidResult>(result);
    }

    [Fact]
    public async Task Delete_IntegrationNotFound_ReturnsNotFound()
    {
        var propertyId = Guid.NewGuid();
        SetUser(OwnerId);
        AllowAuthorization();
        _mockPropertyService.Setup(x => x.GetPropertyAsync(propertyId)).ReturnsAsync(MakeProperty(propertyId));
        _mockOtaService.Setup(x => x.GetIntegrationAsync(It.IsAny<Guid>())).ReturnsAsync((OtaIntegration?)null);

        var result = await _controller.Delete(propertyId, Guid.NewGuid());

        Assert.IsType<NotFoundResult>(result);
    }
}
