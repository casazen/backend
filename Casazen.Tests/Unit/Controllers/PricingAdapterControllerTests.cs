using System.Security.Claims;
using Casazen.Core.Entities;
using Casazen.Core.Services;
using Casazen.Web.BackgroundJobs;
using Casazen.Web.Controllers;
using Casazen.Web.DTOs;
using Hangfire;
using Hangfire.Common;
using Hangfire.States;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Controllers;

public class PricingAdapterControllerTests
{
    private readonly Mock<IPricingAdapterService> _mockPricingService;
    private readonly Mock<IPropertyService> _mockPropertyService;
    private readonly Mock<IBackgroundJobClient> _mockBackgroundJobClient;
    private readonly Mock<ILogger<PricingAdapterController>> _mockLogger;
    private readonly PricingAdapterController _controller;

    private const string OwnerId = "auth0|owner_123";
    private const string OtherId = "auth0|other_456";

    public PricingAdapterControllerTests()
    {
        _mockPricingService = new Mock<IPricingAdapterService>();
        _mockPropertyService = new Mock<IPropertyService>();
        _mockBackgroundJobClient = new Mock<IBackgroundJobClient>();
        _mockLogger = new Mock<ILogger<PricingAdapterController>>();
        _controller = new PricingAdapterController(
            _mockPricingService.Object,
            _mockPropertyService.Object,
            _mockBackgroundJobClient.Object,
            _mockLogger.Object);
    }

    // ─── Helpers ────────────────────────────────────────────────────────────────

    private void SetUser(string userId)
    {
        var identity = new ClaimsIdentity(new[] { new Claim("sub", userId) }, "TestAuth");
        _controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(identity) }
        };
    }

    private void SetAnonymousUser()
    {
        _controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity()) }
        };
    }

    private static Property MakeProperty(Guid id, string ownerId = OwnerId) =>
        new() { Id = id, OwnerId = ownerId, Name = "Test", NightlyRate = 100m };

    private static PricingAdapterConfig MakeConfig(Guid propertyId, bool enabled = true) =>
        new()
        {
            Id = Guid.NewGuid(),
            PropertyId = propertyId,
            IsEnabled = enabled,
            AdaptationFrequency = "daily",
            IncludeSeasonality = true,
            IncludePublicHolidays = false,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

    // ─── GET config ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetConfig_WithValidOwner_ReturnsOk()
    {
        // Arrange
        var propertyId = Guid.NewGuid();
        SetUser(OwnerId);
        _mockPropertyService.Setup(x => x.GetPropertyAsync(propertyId)).ReturnsAsync(MakeProperty(propertyId));
        _mockPricingService.Setup(x => x.GetConfigAsync(propertyId)).ReturnsAsync(MakeConfig(propertyId));

        // Act
        var result = await _controller.GetConfig(propertyId);

        // Assert
        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var dto = Assert.IsType<PricingAdapterConfigResponse>(ok.Value);
        Assert.Equal(propertyId, dto.PropertyId);
    }

    [Fact]
    public async Task GetConfig_WhenConfigNotFound_ReturnsNotFound()
    {
        // Arrange
        var propertyId = Guid.NewGuid();
        SetUser(OwnerId);
        _mockPropertyService.Setup(x => x.GetPropertyAsync(propertyId)).ReturnsAsync(MakeProperty(propertyId));
        _mockPricingService.Setup(x => x.GetConfigAsync(propertyId)).ReturnsAsync((PricingAdapterConfig?)null);

        // Act
        var result = await _controller.GetConfig(propertyId);

        // Assert
        Assert.IsType<NotFoundResult>(result.Result);
    }

    [Fact]
    public async Task GetConfig_WhenPropertyNotFound_ReturnsNotFound()
    {
        // Arrange
        var propertyId = Guid.NewGuid();
        SetUser(OwnerId);
        _mockPropertyService.Setup(x => x.GetPropertyAsync(propertyId)).ReturnsAsync((Property?)null);

        // Act
        var result = await _controller.GetConfig(propertyId);

        // Assert
        Assert.IsType<NotFoundResult>(result.Result);
    }

    [Fact]
    public async Task GetConfig_AsNonOwner_ReturnsForbid()
    {
        // Arrange
        var propertyId = Guid.NewGuid();
        SetUser(OtherId);
        _mockPropertyService.Setup(x => x.GetPropertyAsync(propertyId)).ReturnsAsync(MakeProperty(propertyId, OwnerId));

        // Act
        var result = await _controller.GetConfig(propertyId);

        // Assert
        Assert.IsType<ForbidResult>(result.Result);
        _mockPricingService.Verify(x => x.GetConfigAsync(It.IsAny<Guid>()), Times.Never);
    }

    [Fact]
    public async Task GetConfig_WithoutUserId_ReturnsUnauthorized()
    {
        // Arrange
        SetAnonymousUser();
        var propertyId = Guid.NewGuid();

        // Act
        var result = await _controller.GetConfig(propertyId);

        // Assert
        Assert.IsType<UnauthorizedResult>(result.Result);
    }

    // ─── POST config ────────────────────────────────────────────────────────────

    [Fact]
    public async Task SaveConfig_WithValidRequest_ReturnsOkWithResponse()
    {
        // Arrange
        var propertyId = Guid.NewGuid();
        SetUser(OwnerId);
        _mockPropertyService.Setup(x => x.GetPropertyAsync(propertyId)).ReturnsAsync(MakeProperty(propertyId));
        _mockPricingService.Setup(x => x.GetConfigAsync(propertyId)).ReturnsAsync((PricingAdapterConfig?)null);
        _mockPricingService.Setup(x => x.SaveConfigAsync(It.IsAny<PricingAdapterConfig>()))
            .ReturnsAsync((PricingAdapterConfig c) => c);

        var request = new PricingAdapterConfigRequest
        {
            IsEnabled = true,
            AdaptationFrequency = "daily",
            IncludeSeasonality = true,
            IncludePublicHolidays = false
        };

        // Act
        var result = await _controller.SaveConfig(propertyId, request);

        // Assert
        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var dto = Assert.IsType<PricingAdapterConfigResponse>(ok.Value);
        Assert.True(dto.IsEnabled);
        Assert.Equal("daily", dto.AdaptationFrequency);
        _mockPricingService.Verify(x => x.SaveConfigAsync(It.IsAny<PricingAdapterConfig>()), Times.Once);
    }

    [Fact]
    public async Task SaveConfig_AsNonOwner_ReturnsForbid()
    {
        // Arrange
        var propertyId = Guid.NewGuid();
        SetUser(OtherId);
        _mockPropertyService.Setup(x => x.GetPropertyAsync(propertyId)).ReturnsAsync(MakeProperty(propertyId, OwnerId));

        var request = new PricingAdapterConfigRequest { IsEnabled = true, AdaptationFrequency = "daily" };

        // Act
        var result = await _controller.SaveConfig(propertyId, request);

        // Assert
        Assert.IsType<ForbidResult>(result.Result);
        _mockPricingService.Verify(x => x.SaveConfigAsync(It.IsAny<PricingAdapterConfig>()), Times.Never);
    }

    [Fact]
    public async Task SaveConfig_WithoutUserId_ReturnsUnauthorized()
    {
        // Arrange
        SetAnonymousUser();
        var propertyId = Guid.NewGuid();
        var request = new PricingAdapterConfigRequest { IsEnabled = true, AdaptationFrequency = "daily" };

        // Act
        var result = await _controller.SaveConfig(propertyId, request);

        // Assert
        Assert.IsType<UnauthorizedResult>(result.Result);
    }

    // ─── DELETE config ──────────────────────────────────────────────────────────

    [Fact]
    public async Task DisableConfig_WithValidOwner_ReturnsNoContent()
    {
        // Arrange
        var propertyId = Guid.NewGuid();
        SetUser(OwnerId);
        _mockPropertyService.Setup(x => x.GetPropertyAsync(propertyId)).ReturnsAsync(MakeProperty(propertyId));
        _mockPricingService.Setup(x => x.GetConfigAsync(propertyId)).ReturnsAsync(MakeConfig(propertyId));
        _mockPricingService.Setup(x => x.DisableConfigAsync(propertyId)).Returns(Task.CompletedTask);

        // Act
        var result = await _controller.DisableConfig(propertyId);

        // Assert
        Assert.IsType<NoContentResult>(result);
        _mockPricingService.Verify(x => x.DisableConfigAsync(propertyId), Times.Once);
    }

    [Fact]
    public async Task DisableConfig_WhenConfigNotFound_ReturnsNotFound()
    {
        // Arrange
        var propertyId = Guid.NewGuid();
        SetUser(OwnerId);
        _mockPropertyService.Setup(x => x.GetPropertyAsync(propertyId)).ReturnsAsync(MakeProperty(propertyId));
        _mockPricingService.Setup(x => x.GetConfigAsync(propertyId)).ReturnsAsync((PricingAdapterConfig?)null);

        // Act
        var result = await _controller.DisableConfig(propertyId);

        // Assert
        Assert.IsType<NotFoundResult>(result);
        _mockPricingService.Verify(x => x.DisableConfigAsync(It.IsAny<Guid>()), Times.Never);
    }

    [Fact]
    public async Task DisableConfig_AsNonOwner_ReturnsForbid()
    {
        // Arrange
        var propertyId = Guid.NewGuid();
        SetUser(OtherId);
        _mockPropertyService.Setup(x => x.GetPropertyAsync(propertyId)).ReturnsAsync(MakeProperty(propertyId, OwnerId));

        // Act
        var result = await _controller.DisableConfig(propertyId);

        // Assert
        Assert.IsType<ForbidResult>(result);
    }

    [Fact]
    public async Task DisableConfig_WithoutUserId_ReturnsUnauthorized()
    {
        // Arrange
        SetAnonymousUser();
        var propertyId = Guid.NewGuid();

        // Act
        var result = await _controller.DisableConfig(propertyId);

        // Assert
        Assert.IsType<UnauthorizedResult>(result);
    }

    // ─── GET history ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetHistory_WithValidOwner_ReturnsPagedResponse()
    {
        // Arrange
        var propertyId = Guid.NewGuid();
        SetUser(OwnerId);
        _mockPropertyService.Setup(x => x.GetPropertyAsync(propertyId)).ReturnsAsync(MakeProperty(propertyId));

        var items = new List<PricingHistory>
        {
            new() { Id = Guid.NewGuid(), PropertyId = propertyId, AdaptationDate = DateTime.UtcNow, ChangeReason = "test", SyncStatus = "Synced" },
            new() { Id = Guid.NewGuid(), PropertyId = propertyId, AdaptationDate = DateTime.UtcNow, ChangeReason = "test2", SyncStatus = "Pending" }
        };
        _mockPricingService.Setup(x => x.GetHistoryPagedAsync(propertyId, It.IsAny<DateTime>(), It.IsAny<DateTime>(), 1, 50))
            .ReturnsAsync((items, 2));

        // Act
        var result = await _controller.GetHistory(propertyId, null, null, 1, 50);

        // Assert
        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var paged = Assert.IsType<PricingHistoryPagedResponse>(ok.Value);
        Assert.Equal(2, paged.Total);
        Assert.Equal(1, paged.Page);
        Assert.Equal(2, paged.Items.Count());
    }

    [Fact]
    public async Task GetHistory_AsNonOwner_ReturnsForbid()
    {
        // Arrange
        var propertyId = Guid.NewGuid();
        SetUser(OtherId);
        _mockPropertyService.Setup(x => x.GetPropertyAsync(propertyId)).ReturnsAsync(MakeProperty(propertyId, OwnerId));

        // Act
        var result = await _controller.GetHistory(propertyId, null, null);

        // Assert
        Assert.IsType<ForbidResult>(result.Result);
        _mockPricingService.Verify(x => x.GetHistoryPagedAsync(It.IsAny<Guid>(), It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<int>(), It.IsAny<int>()), Times.Never);
    }

    [Fact]
    public async Task GetHistory_WithoutUserId_ReturnsUnauthorized()
    {
        // Arrange
        SetAnonymousUser();
        var propertyId = Guid.NewGuid();

        // Act
        var result = await _controller.GetHistory(propertyId, null, null);

        // Assert
        Assert.IsType<UnauthorizedResult>(result.Result);
    }

    // ─── POST sync ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task TriggerSync_WithEnabledConfig_ReturnsAcceptedWithJobId()
    {
        // Arrange
        var propertyId = Guid.NewGuid();
        SetUser(OwnerId);
        _mockPropertyService.Setup(x => x.GetPropertyAsync(propertyId)).ReturnsAsync(MakeProperty(propertyId));
        _mockPricingService.Setup(x => x.GetConfigAsync(propertyId)).ReturnsAsync(MakeConfig(propertyId, enabled: true));
        _mockBackgroundJobClient
            .Setup(x => x.Create(It.IsAny<Job>(), It.IsAny<IState>()))
            .Returns("test-job-id");

        // Act
        var result = await _controller.TriggerSync(propertyId);

        // Assert
        var accepted = Assert.IsType<AcceptedResult>(result);
        Assert.NotNull(accepted.Value);
        _mockBackgroundJobClient.Verify(x => x.Create(It.IsAny<Job>(), It.IsAny<IState>()), Times.Once);
    }

    [Fact]
    public async Task TriggerSync_WhenPricingNotEnabled_ReturnsBadRequest()
    {
        // Arrange
        var propertyId = Guid.NewGuid();
        SetUser(OwnerId);
        _mockPropertyService.Setup(x => x.GetPropertyAsync(propertyId)).ReturnsAsync(MakeProperty(propertyId));
        _mockPricingService.Setup(x => x.GetConfigAsync(propertyId)).ReturnsAsync(MakeConfig(propertyId, enabled: false));

        // Act
        var result = await _controller.TriggerSync(propertyId);

        // Assert
        Assert.IsType<BadRequestObjectResult>(result);
        _mockBackgroundJobClient.Verify(x => x.Create(It.IsAny<Job>(), It.IsAny<IState>()), Times.Never);
    }

    [Fact]
    public async Task TriggerSync_WhenConfigMissing_ReturnsBadRequest()
    {
        // Arrange
        var propertyId = Guid.NewGuid();
        SetUser(OwnerId);
        _mockPropertyService.Setup(x => x.GetPropertyAsync(propertyId)).ReturnsAsync(MakeProperty(propertyId));
        _mockPricingService.Setup(x => x.GetConfigAsync(propertyId)).ReturnsAsync((PricingAdapterConfig?)null);

        // Act
        var result = await _controller.TriggerSync(propertyId);

        // Assert
        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task TriggerSync_AsNonOwner_ReturnsForbid()
    {
        // Arrange
        var propertyId = Guid.NewGuid();
        SetUser(OtherId);
        _mockPropertyService.Setup(x => x.GetPropertyAsync(propertyId)).ReturnsAsync(MakeProperty(propertyId, OwnerId));

        // Act
        var result = await _controller.TriggerSync(propertyId);

        // Assert
        Assert.IsType<ForbidResult>(result);
    }

    [Fact]
    public async Task TriggerSync_WithoutUserId_ReturnsUnauthorized()
    {
        // Arrange
        SetAnonymousUser();
        var propertyId = Guid.NewGuid();

        // Act
        var result = await _controller.TriggerSync(propertyId);

        // Assert
        Assert.IsType<UnauthorizedResult>(result);
    }

    // ─── GET preview ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetPreview_WithValidOwner_Returns90DayPreview()
    {
        // Arrange
        var propertyId = Guid.NewGuid();
        SetUser(OwnerId);
        _mockPropertyService.Setup(x => x.GetPropertyAsync(propertyId)).ReturnsAsync(MakeProperty(propertyId));
        _mockPricingService.Setup(x => x.GetConfigAsync(propertyId)).ReturnsAsync(MakeConfig(propertyId));

        var previewData = Enumerable.Range(0, 90).Select(i =>
            (Date: DateTime.UtcNow.Date.AddDays(i), SuggestedPrice: 100m, BasePrice: 100m, Reason: "standard")).ToList();
        _mockPricingService.Setup(x => x.PreviewPricesAsync(propertyId, 100m, It.IsAny<PricingAdapterConfig>()))
            .ReturnsAsync(previewData);

        // Act
        var result = await _controller.GetPreview(propertyId);

        // Assert
        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var preview = Assert.IsType<PricingPreviewResponse>(ok.Value);
        Assert.Equal(90, preview.Prices.Count());
    }

    [Fact]
    public async Task GetPreview_WhenConfigNotFound_ReturnsNotFound()
    {
        // Arrange
        var propertyId = Guid.NewGuid();
        SetUser(OwnerId);
        _mockPropertyService.Setup(x => x.GetPropertyAsync(propertyId)).ReturnsAsync(MakeProperty(propertyId));
        _mockPricingService.Setup(x => x.GetConfigAsync(propertyId)).ReturnsAsync((PricingAdapterConfig?)null);

        // Act
        var result = await _controller.GetPreview(propertyId);

        // Assert
        Assert.IsType<NotFoundResult>(result.Result);
    }

    [Fact]
    public async Task GetPreview_AsNonOwner_ReturnsForbid()
    {
        // Arrange
        var propertyId = Guid.NewGuid();
        SetUser(OtherId);
        _mockPropertyService.Setup(x => x.GetPropertyAsync(propertyId)).ReturnsAsync(MakeProperty(propertyId, OwnerId));

        // Act
        var result = await _controller.GetPreview(propertyId);

        // Assert
        Assert.IsType<ForbidResult>(result.Result);
    }

    [Fact]
    public async Task GetPreview_WithoutUserId_ReturnsUnauthorized()
    {
        // Arrange
        SetAnonymousUser();
        var propertyId = Guid.NewGuid();

        // Act
        var result = await _controller.GetPreview(propertyId);

        // Assert
        Assert.IsType<UnauthorizedResult>(result.Result);
    }
}
