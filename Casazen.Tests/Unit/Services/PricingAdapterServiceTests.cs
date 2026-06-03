using Casazen.Core.Entities;
using Casazen.Core.Repositories;
using Casazen.Core.Services;
using Casazen.Infrastructure.Services;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Services;

public class PricingAdapterServiceTests
{
    private readonly Mock<IPricingAdapterConfigRepository> _mockConfigRepository;
    private readonly Mock<IPricingHistoryRepository> _mockHistoryRepository;
    private readonly Mock<IPublicHolidayService> _mockPublicHolidayService;
    private readonly PricingAdapterService _service;

    public PricingAdapterServiceTests()
    {
        _mockConfigRepository = new Mock<IPricingAdapterConfigRepository>();
        _mockHistoryRepository = new Mock<IPricingHistoryRepository>();
        _mockPublicHolidayService = new Mock<IPublicHolidayService>();
        _service = new PricingAdapterService(
            _mockConfigRepository.Object,
            _mockHistoryRepository.Object,
            _mockPublicHolidayService.Object,
            new Mock<ILogger<PricingAdapterService>>().Object);
    }

    #region Seasonality Tests

    [Theory]
    [InlineData(6, 1.3)] // June - high season
    [InlineData(7, 1.3)] // July - high season
    [InlineData(8, 1.3)] // August - high season
    [InlineData(11, 0.8)] // November - low season
    [InlineData(12, 0.8)] // December - low season
    [InlineData(1, 0.8)] // January - low season
    [InlineData(2, 0.8)] // February - low season
    [InlineData(3, 1.0)] // March - shoulder season
    [InlineData(4, 1.0)] // April - shoulder season
    [InlineData(5, 1.0)] // May - shoulder season
    [InlineData(9, 1.0)] // September - shoulder season
    [InlineData(10, 1.0)] // October - shoulder season
    public async Task CalculatePricingMultiplierAsync_SeasonalityOnly_ReturnsCorrectMultiplier(int month, decimal expectedMultiplier)
    {
        // Arrange
        var date = new DateTime(2024, month, 15, 12, 0, 0, DateTimeKind.Utc);
        _mockPublicHolidayService.Setup(x => x.IsPublicHolidayAsync(date)).ReturnsAsync(false);

        // Act
        var multiplier = await _service.CalculatePricingMultiplierAsync(date, includeSeasonality: true, includePublicHolidays: false);

        // Assert
        Assert.Equal(expectedMultiplier, multiplier);
    }

    [Fact]
    public async Task CalculatePricingMultiplierAsync_NoSeasonality_Returns1Point0()
    {
        // Arrange
        var date = new DateTime(2024, 7, 15, 12, 0, 0, DateTimeKind.Utc); // High season month
        _mockPublicHolidayService.Setup(x => x.IsPublicHolidayAsync(date)).ReturnsAsync(false);

        // Act
        var multiplier = await _service.CalculatePricingMultiplierAsync(date, includeSeasonality: false, includePublicHolidays: false);

        // Assert
        Assert.Equal(1.0m, multiplier);
    }

    #endregion

    #region Public Holiday Tests

    [Fact]
    public async Task CalculatePricingMultiplierAsync_PublicHoliday_AppliesMultiplier()
    {
        // Arrange
        var date = new DateTime(2024, 1, 1, 12, 0, 0, DateTimeKind.Utc); // New Year's Day
        _mockPublicHolidayService.Setup(x => x.IsPublicHolidayAsync(date)).ReturnsAsync(true);

        // Act
        var multiplier = await _service.CalculatePricingMultiplierAsync(date, includeSeasonality: false, includePublicHolidays: true);

        // Assert
        Assert.Equal(1.5m, multiplier);
    }

    [Fact]
    public async Task CalculatePricingMultiplierAsync_NotPublicHoliday_DoesNotApplyMultiplier()
    {
        // Arrange
        var date = new DateTime(2024, 3, 15, 12, 0, 0, DateTimeKind.Utc); // Regular day
        _mockPublicHolidayService.Setup(x => x.IsPublicHolidayAsync(date)).ReturnsAsync(false);

        // Act
        var multiplier = await _service.CalculatePricingMultiplierAsync(date, includeSeasonality: false, includePublicHolidays: true);

        // Assert
        Assert.Equal(1.0m, multiplier);
    }

    [Fact]
    public async Task CalculatePricingMultiplierAsync_PublicHolidayWithoutIncludeFlag_DoesNotApplyMultiplier()
    {
        // Arrange
        var date = new DateTime(2024, 1, 1, 12, 0, 0, DateTimeKind.Utc); // New Year's Day
        _mockPublicHolidayService.Setup(x => x.IsPublicHolidayAsync(date)).ReturnsAsync(true);

        // Act
        var multiplier = await _service.CalculatePricingMultiplierAsync(date, includeSeasonality: false, includePublicHolidays: false);

        // Assert
        Assert.Equal(1.0m, multiplier);
    }

    #endregion

    #region Combined Seasonality and Holiday Tests

    [Fact]
    public async Task CalculatePricingMultiplierAsync_HighSeasonAndPublicHoliday_MultiplyBoth()
    {
        // Arrange - July 15 (high season) as a public holiday
        var date = new DateTime(2024, 7, 15, 12, 0, 0, DateTimeKind.Utc);
        _mockPublicHolidayService.Setup(x => x.IsPublicHolidayAsync(date)).ReturnsAsync(true);

        // Act
        var multiplier = await _service.CalculatePricingMultiplierAsync(date, includeSeasonality: true, includePublicHolidays: true);

        // Assert - 1.5 (holiday) * 1.3 (high season) = 1.95
        Assert.Equal(1.95m, multiplier);
    }

    [Fact]
    public async Task CalculatePricingMultiplierAsync_LowSeasonAndPublicHoliday_MultiplyBoth()
    {
        // Arrange - January 1 (low season) as a public holiday
        var date = new DateTime(2024, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        _mockPublicHolidayService.Setup(x => x.IsPublicHolidayAsync(date)).ReturnsAsync(true);

        // Act
        var multiplier = await _service.CalculatePricingMultiplierAsync(date, includeSeasonality: true, includePublicHolidays: true);

        // Assert - 1.5 (holiday) * 0.8 (low season) = 1.2
        Assert.Equal(1.2m, multiplier);
    }

    #endregion

    #region Config Management Tests

    [Fact]
    public async Task GetConfigAsync_WithExistingConfig_ReturnsConfig()
    {
        // Arrange
        var propertyId = Guid.NewGuid();
        var config = new PricingAdapterConfig { PropertyId = propertyId, IsEnabled = true };
        _mockConfigRepository.Setup(x => x.GetByPropertyIdAsync(propertyId)).ReturnsAsync(config);

        // Act
        var result = await _service.GetConfigAsync(propertyId);

        // Assert
        Assert.NotNull(result);
        Assert.Equal(propertyId, result.PropertyId);
        _mockConfigRepository.Verify(x => x.GetByPropertyIdAsync(propertyId), Times.Once);
    }

    [Fact]
    public async Task SaveConfigAsync_WithNewConfig_AddsConfig()
    {
        // Arrange
        var config = new PricingAdapterConfig { Id = Guid.Empty, PropertyId = Guid.NewGuid(), IsEnabled = true };
        _mockConfigRepository.Setup(x => x.AddAsync(It.IsAny<PricingAdapterConfig>())).ReturnsAsync(config);

        // Act
        var result = await _service.SaveConfigAsync(config);

        // Assert
        Assert.NotNull(result);
        _mockConfigRepository.Verify(x => x.AddAsync(It.IsAny<PricingAdapterConfig>()), Times.Once);
    }

    [Fact]
    public async Task SaveConfigAsync_WithExistingConfig_UpdatesConfig()
    {
        // Arrange
        var config = new PricingAdapterConfig
        {
            Id = Guid.NewGuid(),
            PropertyId = Guid.NewGuid(),
            IsEnabled = true
        };

        // Act
        var result = await _service.SaveConfigAsync(config);

        // Assert
        Assert.NotNull(result);
        _mockConfigRepository.Verify(x => x.UpdateAsync(It.IsAny<PricingAdapterConfig>()), Times.Once);
    }

    #endregion

    #region Pricing History Tests

    [Fact]
    public async Task RecordPricingChangeAsync_WithValidData_CreatesPricingHistory()
    {
        // Arrange
        var propertyId = Guid.NewGuid();
        var previousPrice = 100m;
        var newPrice = 130m;
        var reason = "High season adjustment";
        var confidence = 0.95m;

        PricingHistory? capturedHistory = null;
        _mockHistoryRepository.Setup(x => x.AddAsync(It.IsAny<PricingHistory>()))
            .Callback<PricingHistory>(h => capturedHistory = h)
            .ReturnsAsync((PricingHistory h) => h);

        // Act
        var result = await _service.RecordPricingChangeAsync(
            propertyId,
            previousPrice,
            newPrice,
            reason,
            confidence);

        // Assert
        Assert.NotNull(result);
        Assert.Equal(propertyId, result.PropertyId);
        Assert.Equal(previousPrice, result.PreviousPrice);
        Assert.Equal(newPrice, result.NewPrice);
        Assert.Equal(reason, result.ChangeReason);
        Assert.Equal(confidence, result.AiConfidence);
        Assert.Equal("Pending", result.SyncStatus);
        _mockHistoryRepository.Verify(x => x.AddAsync(It.IsAny<PricingHistory>()), Times.Once);
    }

    [Fact]
    public async Task RecordPricingChangeAsync_WithOtasSynced_IncludesOtasInRecord()
    {
        // Arrange
        var propertyId = Guid.NewGuid();
        var otasSynced = "[\"Airbnb\", \"Booking\"]";

        PricingHistory? capturedHistory = null;
        _mockHistoryRepository.Setup(x => x.AddAsync(It.IsAny<PricingHistory>()))
            .Callback<PricingHistory>(h => capturedHistory = h)
            .ReturnsAsync((PricingHistory h) => h);

        // Act
        var result = await _service.RecordPricingChangeAsync(
            propertyId,
            100m,
            130m,
            "Test",
            0.9m,
            otasSynced,
            "Synced");

        // Assert
        Assert.Equal(otasSynced, result.OtasSynced);
        Assert.Equal("Synced", result.SyncStatus);
    }

    #endregion
}
