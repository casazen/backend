using System.Security.Claims;
using Casazen.Core.Entities;
using Casazen.Core.Services;
using Casazen.Core.Pricing;
using Casazen.Tests.Unit.Authorization;
using Casazen.Web.Controllers;
using Casazen.Web.DTOs;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Controllers;

public class PricingAdapterControllerTests
{
    private readonly Mock<IPricingAdapterService> _mockPricingService;
    private readonly Mock<IPropertyService> _mockPropertyService;
    private readonly Mock<ILogger<PricingAdapterController>> _mockLogger;
    private readonly PricingAdapterController _controller;

    private const string OwnerId = "auth0|owner_123";
    private const string OtherId = "auth0|other_456";
    private static readonly Guid TestOrgId = Guid.NewGuid();

    public PricingAdapterControllerTests()
    {
        _mockPricingService = new Mock<IPricingAdapterService>();
        _mockPropertyService = new Mock<IPropertyService>();
        _mockLogger = new Mock<ILogger<PricingAdapterController>>();
        _controller = new PricingAdapterController(
            _mockPricingService.Object,
            _mockPropertyService.Object,
            HostAuthorizationTestHarness.Create(TestOrgId),
            _mockLogger.Object);
    }

    // Authorization is the real host resource handler (TN-3): the owner of a property of TestOrgId is allowed,
    // anyone else (another user of the org without an org-wide role, an anonymous caller) is not.

    // ─── Helpers ────────────────────────────────────────────────────────────────

    private void SetUser(string userId)
    {
        var identity = new ClaimsIdentity(new[] { new Claim("sub", userId) }, "TestAuth");
        _controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(identity),
                RequestServices = new ServiceCollection().AddLogging().AddLocalization().BuildServiceProvider(),
            }
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
        new() { Id = id, OwnerId = ownerId, OrgId = TestOrgId, Name = "Test", NightlyRate = 180m };

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
    public async Task GetConfig_WhenConfigNotFound_ReturnsDefaultDisabledConfig()
    {
        // Arrange
        var propertyId = Guid.NewGuid();
        SetUser(OwnerId);
        _mockPropertyService.Setup(x => x.GetPropertyAsync(propertyId)).ReturnsAsync(MakeProperty(propertyId));
        _mockPricingService.Setup(x => x.GetConfigAsync(propertyId)).ReturnsAsync((PricingAdapterConfig?)null);

        // Act
        var result = await _controller.GetConfig(propertyId);

        // Assert
        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var dto = Assert.IsType<PricingAdapterConfigResponse>(ok.Value);
        Assert.Equal(propertyId, dto.PropertyId);
        Assert.False(dto.IsEnabled);
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
    public async Task GetConfig_WithoutUserId_ReturnsForbid()
    {
        // Arrange
        SetAnonymousUser();
        var propertyId = Guid.NewGuid();
        _mockPropertyService.Setup(x => x.GetPropertyAsync(propertyId)).ReturnsAsync(MakeProperty(propertyId));

        // Act
        var result = await _controller.GetConfig(propertyId);

        // Assert
        Assert.IsType<ForbidResult>(result.Result);
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
        var result = await _controller.SaveConfig(propertyId, request, CancellationToken.None);

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
        var result = await _controller.SaveConfig(propertyId, request, CancellationToken.None);

        // Assert
        Assert.IsType<ForbidResult>(result.Result);
        _mockPricingService.Verify(x => x.SaveConfigAsync(It.IsAny<PricingAdapterConfig>()), Times.Never);
    }

    [Fact]
    public async Task SaveConfig_WithoutUserId_ReturnsForbid()
    {
        // Arrange
        SetAnonymousUser();
        var propertyId = Guid.NewGuid();
        _mockPropertyService.Setup(x => x.GetPropertyAsync(propertyId)).ReturnsAsync(MakeProperty(propertyId));
        var request = new PricingAdapterConfigRequest { IsEnabled = true, AdaptationFrequency = "daily" };

        // Act
        var result = await _controller.SaveConfig(propertyId, request, CancellationToken.None);

        // Assert
        Assert.IsType<ForbidResult>(result.Result);
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
        _mockPricingService.Setup(x => x.DisableConfigAsync(propertyId, It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

        // Act
        var result = await _controller.DisableConfig(propertyId, CancellationToken.None);

        // Assert
        Assert.IsType<NoContentResult>(result);
        _mockPricingService.Verify(x => x.DisableConfigAsync(propertyId, It.IsAny<CancellationToken>()), Times.Once);
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
        var result = await _controller.DisableConfig(propertyId, CancellationToken.None);

        // Assert
        Assert.IsType<NotFoundResult>(result);
        _mockPricingService.Verify(x => x.DisableConfigAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task DisableConfig_AsNonOwner_ReturnsForbid()
    {
        // Arrange
        var propertyId = Guid.NewGuid();
        SetUser(OtherId);
        _mockPropertyService.Setup(x => x.GetPropertyAsync(propertyId)).ReturnsAsync(MakeProperty(propertyId, OwnerId));

        // Act
        var result = await _controller.DisableConfig(propertyId, CancellationToken.None);

        // Assert
        Assert.IsType<ForbidResult>(result);
    }

    [Fact]
    public async Task DisableConfig_WithoutUserId_ReturnsForbid()
    {
        // Arrange
        SetAnonymousUser();
        var propertyId = Guid.NewGuid();
        _mockPropertyService.Setup(x => x.GetPropertyAsync(propertyId)).ReturnsAsync(MakeProperty(propertyId));

        // Act
        var result = await _controller.DisableConfig(propertyId, CancellationToken.None);

        // Assert
        Assert.IsType<ForbidResult>(result);
    }

    // ─── POST config: rules and immediate computation (PC-15) ───────────────────

    [Fact]
    public async Task SaveConfig_EnabledWithRules_StoresThemAndComputesTheSuggestionsNow()
    {
        var propertyId = Guid.NewGuid();
        SetUser(OwnerId);
        _mockPropertyService.Setup(x => x.GetPropertyAsync(propertyId)).ReturnsAsync(MakeProperty(propertyId));
        _mockPricingService.Setup(x => x.GetConfigAsync(propertyId)).ReturnsAsync((PricingAdapterConfig?)null);
        PricingAdapterConfig? saved = null;
        _mockPricingService.Setup(x => x.SaveConfigAsync(It.IsAny<PricingAdapterConfig>()))
            .Callback<PricingAdapterConfig>(c => saved = c)
            .ReturnsAsync((PricingAdapterConfig c) => c);
        var request = new PricingAdapterConfigRequest
        {
            IsEnabled = true,
            AdaptationFrequency = "weekly",
            IncludeSeasonality = true,
            IncludePublicHolidays = true,
            HighSeasonMonths = [8, 7],
            HighSeasonMultiplier = 1.25m,
            LowSeasonMonths = [1],
            LowSeasonMultiplier = 0.9m,
            HolidayMultiplier = 1.4m,
        };

        var result = await _controller.SaveConfig(propertyId, request, CancellationToken.None);

        var dto = Assert.IsType<PricingAdapterConfigResponse>(Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.Equal(new[] { 7, 8 }, dto.HighSeasonMonths);
        Assert.Equal(1.25m, dto.HighSeasonMultiplier);
        Assert.Equal(new[] { 1 }, saved!.LowSeasonMonths);
        Assert.Equal(1.4m, saved.HolidayMultiplier);
        Assert.Equal(TestOrgId, saved.OrgId);
        _mockPricingService.Verify(x => x.RegenerateSuggestionsAsync(propertyId, false, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task SaveConfig_NewConfigWithoutRules_StartsFromTheExampleRule()
    {
        var propertyId = Guid.NewGuid();
        SetUser(OwnerId);
        _mockPropertyService.Setup(x => x.GetPropertyAsync(propertyId)).ReturnsAsync(MakeProperty(propertyId));
        _mockPricingService.Setup(x => x.GetConfigAsync(propertyId)).ReturnsAsync((PricingAdapterConfig?)null);
        _mockPricingService.Setup(x => x.SaveConfigAsync(It.IsAny<PricingAdapterConfig>()))
            .ReturnsAsync((PricingAdapterConfig c) => c);

        var result = await _controller.SaveConfig(
            propertyId, new PricingAdapterConfigRequest { IsEnabled = true, AdaptationFrequency = "daily" }, CancellationToken.None);

        var dto = Assert.IsType<PricingAdapterConfigResponse>(Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.Equal(SeasonalPricingRules.ExampleHighSeasonMonths, dto.HighSeasonMonths);
        Assert.Equal(SeasonalPricingRules.ExampleHolidayMultiplier, dto.HolidayMultiplier);
    }

    [Fact]
    public async Task SaveConfig_Disabled_RemovesTheSuggestionsInsteadOfComputing()
    {
        var propertyId = Guid.NewGuid();
        SetUser(OwnerId);
        _mockPropertyService.Setup(x => x.GetPropertyAsync(propertyId)).ReturnsAsync(MakeProperty(propertyId));
        _mockPricingService.Setup(x => x.GetConfigAsync(propertyId)).ReturnsAsync(MakeConfig(propertyId));
        _mockPricingService.Setup(x => x.SaveConfigAsync(It.IsAny<PricingAdapterConfig>()))
            .ReturnsAsync((PricingAdapterConfig c) => c);

        await _controller.SaveConfig(
            propertyId, new PricingAdapterConfigRequest { IsEnabled = false, AdaptationFrequency = "daily" }, CancellationToken.None);

        _mockPricingService.Verify(x => x.DisableConfigAsync(propertyId, It.IsAny<CancellationToken>()), Times.Once);
        _mockPricingService.Verify(
            x => x.RegenerateSuggestionsAsync(It.IsAny<Guid>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task SaveConfig_LowSeasonOverlapsStoredHighSeason_ReturnsBadRequest()
    {
        var propertyId = Guid.NewGuid();
        SetUser(OwnerId);
        _mockPropertyService.Setup(x => x.GetPropertyAsync(propertyId)).ReturnsAsync(MakeProperty(propertyId));
        _mockPricingService.Setup(x => x.GetConfigAsync(propertyId)).ReturnsAsync(MakeConfig(propertyId));

        var result = await _controller.SaveConfig(
            propertyId,
            new PricingAdapterConfigRequest { IsEnabled = true, AdaptationFrequency = "daily", LowSeasonMonths = [7] },
            CancellationToken.None);

        Assert.Equal(StatusCodes.Status400BadRequest, Assert.IsType<ObjectResult>(result.Result).StatusCode);
        _mockPricingService.Verify(x => x.SaveConfigAsync(It.IsAny<PricingAdapterConfig>()), Times.Never);
    }

    // ─── GET suggestions ────────────────────────────────────────────────────────

    [Fact]
    public async Task GetSuggestions_Enabled_ReturnsRowsWithTheRealCurrentBasePrice()
    {
        var propertyId = Guid.NewGuid();
        SetUser(OwnerId);
        _mockPropertyService.Setup(x => x.GetPropertyAsync(propertyId)).ReturnsAsync(MakeProperty(propertyId));
        var config = MakeConfig(propertyId);
        config.AdaptationFrequency = "weekly";
        config.LastAdaptedAt = new DateTime(2026, 7, 1, 2, 0, 0, DateTimeKind.Utc);
        _mockPricingService.Setup(x => x.GetConfigAsync(propertyId)).ReturnsAsync(config);
        _mockPricingService.Setup(x => x.GetSuggestionsAsync(propertyId, It.IsAny<CancellationToken>())).ReturnsAsync(
        new List<SeasonalPriceSuggestion>
        {
            new SeasonalPriceSuggestion
            {
                PropertyId = propertyId,
                StayDate = new DateOnly(2026, 7, 14),
                BasePrice = 180m,
                SuggestedPrice = 234m,
                Multiplier = 1.30m,
                Rule = SeasonalPriceRule.HighSeason,
            },
        });

        var result = await _controller.GetSuggestions(propertyId, CancellationToken.None);

        var dto = Assert.IsType<SeasonalSuggestionsResponse>(Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.True(dto.IsEnabled);
        Assert.Equal(180m, dto.CurrentBasePrice);
        Assert.Equal(new DateOnly(2026, 7, 8), dto.NextRunOn);
        var item = Assert.Single(dto.Items);
        Assert.Equal(234m, item.SuggestedPrice);
        Assert.Equal(SeasonalPriceRule.HighSeason, item.Rule);
    }

    [Fact]
    public async Task GetSuggestions_Disabled_ReturnsNoItems()
    {
        var propertyId = Guid.NewGuid();
        SetUser(OwnerId);
        _mockPropertyService.Setup(x => x.GetPropertyAsync(propertyId)).ReturnsAsync(MakeProperty(propertyId));
        _mockPricingService.Setup(x => x.GetConfigAsync(propertyId)).ReturnsAsync(MakeConfig(propertyId, enabled: false));

        var result = await _controller.GetSuggestions(propertyId, CancellationToken.None);

        var dto = Assert.IsType<SeasonalSuggestionsResponse>(Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.False(dto.IsEnabled);
        Assert.Empty(dto.Items);
        _mockPricingService.Verify(x => x.GetSuggestionsAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task GetSuggestions_AsNonOwner_ReturnsForbid()
    {
        var propertyId = Guid.NewGuid();
        SetUser(OtherId);
        _mockPropertyService.Setup(x => x.GetPropertyAsync(propertyId)).ReturnsAsync(MakeProperty(propertyId, OwnerId));

        var result = await _controller.GetSuggestions(propertyId, CancellationToken.None);

        Assert.IsType<ForbidResult>(result.Result);
    }

    // ─── POST recalculate ───────────────────────────────────────────────────────

    [Fact]
    public async Task Recalculate_Enabled_RunsTheSameComputationAsTheJobWithoutTheDueCheck()
    {
        var propertyId = Guid.NewGuid();
        SetUser(OwnerId);
        var computedAt = new DateTime(2026, 7, 1, 10, 0, 0, DateTimeKind.Utc);
        _mockPropertyService.Setup(x => x.GetPropertyAsync(propertyId)).ReturnsAsync(MakeProperty(propertyId));
        _mockPricingService.Setup(x => x.RegenerateSuggestionsAsync(propertyId, false, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SeasonalSuggestionRunResult(SeasonalSuggestionRunStatus.Computed, 90, computedAt));

        var result = await _controller.Recalculate(propertyId, CancellationToken.None);

        var dto = Assert.IsType<SeasonalSuggestionRunResponse>(Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.Equal(SeasonalSuggestionRunStatus.Computed, dto.Status);
        Assert.Equal(90, dto.Days);
        Assert.Equal(computedAt, dto.ComputedAt);
    }

    [Fact]
    public async Task Recalculate_NotEnabled_Returns422WithStableCode()
    {
        var propertyId = Guid.NewGuid();
        SetUser(OwnerId);
        _mockPropertyService.Setup(x => x.GetPropertyAsync(propertyId)).ReturnsAsync(MakeProperty(propertyId));
        _mockPricingService.Setup(x => x.RegenerateSuggestionsAsync(propertyId, false, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SeasonalSuggestionRunResult(SeasonalSuggestionRunStatus.NotEnabled, 0, null));

        var result = await _controller.Recalculate(propertyId, CancellationToken.None);

        var problem = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(StatusCodes.Status422UnprocessableEntity, problem.StatusCode);
        var details = Assert.IsType<ProblemDetails>(problem.Value);
        Assert.Equal(PricingAdapterController.SuggestionsNotEnabledCode, details.Extensions["code"]);
    }

    [Fact]
    public async Task Recalculate_AsNonOwner_ReturnsForbidWithoutComputing()
    {
        var propertyId = Guid.NewGuid();
        SetUser(OtherId);
        _mockPropertyService.Setup(x => x.GetPropertyAsync(propertyId)).ReturnsAsync(MakeProperty(propertyId, OwnerId));

        var result = await _controller.Recalculate(propertyId, CancellationToken.None);

        Assert.IsType<ForbidResult>(result.Result);
        _mockPricingService.Verify(
            x => x.RegenerateSuggestionsAsync(It.IsAny<Guid>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Recalculate_PropertyOfAnotherOrg_ReturnsNotFound()
    {
        var propertyId = Guid.NewGuid();
        SetUser(OwnerId);
        _mockPropertyService.Setup(x => x.GetPropertyAsync(propertyId)).ReturnsAsync((Property?)null);

        var result = await _controller.Recalculate(propertyId, CancellationToken.None);

        Assert.IsType<NotFoundResult>(result.Result);
    }
}
