using Casazen.Core.Entities;
using Casazen.Core.Repositories;
using Casazen.Core.Services;
using Casazen.Web.BackgroundJobs;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.BackgroundJobs;

public class DynamicPricingJobTests
{
    private readonly Mock<IPricingAdapterConfigRepository> _configRepositoryMock;
    private readonly Mock<IPricingHistoryRepository> _historyRepositoryMock;
    private readonly Mock<IPricingAdapterService> _pricingServiceMock;
    private readonly Mock<IOtaManager> _otaManagerMock;
    private readonly Mock<ILogger<DynamicPricingJob>> _loggerMock;
    private readonly DynamicPricingJob _job;

    public DynamicPricingJobTests()
    {
        _configRepositoryMock = new Mock<IPricingAdapterConfigRepository>();
        _historyRepositoryMock = new Mock<IPricingHistoryRepository>();
        _pricingServiceMock = new Mock<IPricingAdapterService>();
        _otaManagerMock = new Mock<IOtaManager>();
        _loggerMock = new Mock<ILogger<DynamicPricingJob>>();

        _job = new DynamicPricingJob(
            _configRepositoryMock.Object,
            _historyRepositoryMock.Object,
            _pricingServiceMock.Object,
            _otaManagerMock.Object,
            _loggerMock.Object);
    }

    [Fact]
    public async Task ExecuteAsync_NoEnabledConfigs_CompletesSuccessfully()
    {
        // Arrange
        _configRepositoryMock.Setup(r => r.GetEnabledConfigsAsync())
            .ReturnsAsync(new List<PricingAdapterConfig>());

        // Act
        await _job.ExecuteAsync();

        // Assert
        _configRepositoryMock.Verify(r => r.GetEnabledConfigsAsync(), Times.Once);
        _historyRepositoryMock.Verify(r => r.AddAsync(It.IsAny<PricingHistory>()), Times.Never);
    }

    [Fact]
    public async Task ExecuteAsync_SingleEnabledConfig_ProcessesSuccessfully()
    {
        // Arrange
        var propertyId = Guid.NewGuid();
        var config = new PricingAdapterConfig
        {
            Id = Guid.NewGuid(),
            PropertyId = propertyId,
            IsEnabled = true,
            IncludeSeasonality = true,
            IncludePublicHolidays = false,
            AdaptationFrequency = "daily"
        };

        _configRepositoryMock.Setup(r => r.GetEnabledConfigsAsync())
            .ReturnsAsync(new[] { config });

        _pricingServiceMock.Setup(s => s.CalculatePricingMultiplierAsync(
            It.IsAny<DateTime>(),
            It.IsAny<bool>(),
            It.IsAny<bool>()))
            .ReturnsAsync(1.2m);

        _historyRepositoryMock.Setup(r => r.AddAsync(It.IsAny<PricingHistory>()))
            .ReturnsAsync((PricingHistory h) => h);

        _otaManagerMock.Setup(m => m.SyncAllAsync(propertyId))
            .ReturnsAsync(true);

        // Act
        await _job.ExecuteAsync();

        // Assert
        _configRepositoryMock.Verify(r => r.GetEnabledConfigsAsync(), Times.Once);
        _pricingServiceMock.Verify(
            s => s.CalculatePricingMultiplierAsync(It.IsAny<DateTime>(), true, false),
            Times.AtLeastOnce);
        _historyRepositoryMock.Verify(
            r => r.AddAsync(It.IsAny<PricingHistory>()),
            Times.AtLeastOnce);
        _configRepositoryMock.Verify(r => r.UpdateAsync(config), Times.Once);
        _otaManagerMock.Verify(m => m.SyncAllAsync(propertyId), Times.Once);
    }

    [Fact]
    public async Task ExecuteAsync_MultipleConfigs_AllProcessed()
    {
        // Arrange
        var propertyId1 = Guid.NewGuid();
        var propertyId2 = Guid.NewGuid();
        var config1 = new PricingAdapterConfig
        {
            Id = Guid.NewGuid(),
            PropertyId = propertyId1,
            IsEnabled = true,
            IncludeSeasonality = true,
            IncludePublicHolidays = true,
            AdaptationFrequency = "daily"
        };
        var config2 = new PricingAdapterConfig
        {
            Id = Guid.NewGuid(),
            PropertyId = propertyId2,
            IsEnabled = true,
            IncludeSeasonality = false,
            IncludePublicHolidays = false,
            AdaptationFrequency = "daily"
        };

        _configRepositoryMock.Setup(r => r.GetEnabledConfigsAsync())
            .ReturnsAsync(new[] { config1, config2 });

        _pricingServiceMock.Setup(s => s.CalculatePricingMultiplierAsync(
            It.IsAny<DateTime>(),
            It.IsAny<bool>(),
            It.IsAny<bool>()))
            .ReturnsAsync(1.1m);

        _historyRepositoryMock.Setup(r => r.AddAsync(It.IsAny<PricingHistory>()))
            .ReturnsAsync((PricingHistory h) => h);

        _otaManagerMock.Setup(m => m.SyncAllAsync(It.IsAny<Guid>()))
            .ReturnsAsync(true);

        // Act
        await _job.ExecuteAsync();

        // Assert
        _configRepositoryMock.Verify(r => r.GetEnabledConfigsAsync(), Times.Once);
        _configRepositoryMock.Verify(r => r.UpdateAsync(It.IsAny<PricingAdapterConfig>()), Times.Exactly(2));
        _otaManagerMock.Verify(m => m.SyncAllAsync(It.IsAny<Guid>()), Times.Exactly(2));
    }

    [Fact]
    public async Task ExecuteAsync_SinglePropertyFails_ContinuesWithNextProperty()
    {
        // Arrange
        var propertyId1 = Guid.NewGuid();
        var propertyId2 = Guid.NewGuid();
        var config1 = new PricingAdapterConfig
        {
            Id = Guid.NewGuid(),
            PropertyId = propertyId1,
            IsEnabled = true,
            IncludeSeasonality = true,
            IncludePublicHolidays = false,
            AdaptationFrequency = "daily"
        };
        var config2 = new PricingAdapterConfig
        {
            Id = Guid.NewGuid(),
            PropertyId = propertyId2,
            IsEnabled = true,
            IncludeSeasonality = false,
            IncludePublicHolidays = false,
            AdaptationFrequency = "daily"
        };

        _configRepositoryMock.Setup(r => r.GetEnabledConfigsAsync())
            .ReturnsAsync(new[] { config1, config2 });

        _pricingServiceMock.Setup(s => s.CalculatePricingMultiplierAsync(
            It.IsAny<DateTime>(),
            It.IsAny<bool>(),
            It.IsAny<bool>()))
            .ReturnsAsync(1.1m);

        _historyRepositoryMock.Setup(r => r.AddAsync(It.IsAny<PricingHistory>()))
            .ReturnsAsync((PricingHistory h) => h);

        _otaManagerMock.Setup(m => m.SyncAllAsync(propertyId1))
            .ThrowsAsync(new Exception("OTA sync failed"));

        _otaManagerMock.Setup(m => m.SyncAllAsync(propertyId2))
            .ReturnsAsync(true);

        // Act
        await _job.ExecuteAsync();

        // Assert - Property 2 should still be processed despite Property 1 failure
        _configRepositoryMock.Verify(r => r.UpdateAsync(config2), Times.Once);
        _otaManagerMock.Verify(m => m.SyncAllAsync(propertyId2), Times.Once);
    }

    [Fact]
    public async Task ExecuteAsync_UpdatesLastAdaptedAtAndNextScheduledRunAt()
    {
        // Arrange
        var propertyId = Guid.NewGuid();
        var config = new PricingAdapterConfig
        {
            Id = Guid.NewGuid(),
            PropertyId = propertyId,
            IsEnabled = true,
            IncludeSeasonality = true,
            IncludePublicHolidays = false,
            AdaptationFrequency = "daily",
            LastAdaptedAt = null,
            NextScheduledRunAt = null
        };

        _configRepositoryMock.Setup(r => r.GetEnabledConfigsAsync())
            .ReturnsAsync(new[] { config });

        _pricingServiceMock.Setup(s => s.CalculatePricingMultiplierAsync(
            It.IsAny<DateTime>(),
            It.IsAny<bool>(),
            It.IsAny<bool>()))
            .ReturnsAsync(1.0m);

        _historyRepositoryMock.Setup(r => r.AddAsync(It.IsAny<PricingHistory>()))
            .ReturnsAsync((PricingHistory h) => h);

        _otaManagerMock.Setup(m => m.SyncAllAsync(propertyId))
            .ReturnsAsync(true);

        var capturedConfig = (PricingAdapterConfig?)null;
        _configRepositoryMock.Setup(r => r.UpdateAsync(It.IsAny<PricingAdapterConfig>()))
            .Callback<PricingAdapterConfig>(c => capturedConfig = c)
            .Returns(Task.CompletedTask);

        // Act
        var beforeRun = DateTime.UtcNow;
        await _job.ExecuteAsync();
        var afterRun = DateTime.UtcNow;

        // Assert
        Assert.NotNull(capturedConfig);
        Assert.NotNull(capturedConfig.LastAdaptedAt);
        Assert.True(capturedConfig.LastAdaptedAt >= beforeRun && capturedConfig.LastAdaptedAt <= afterRun);
        Assert.NotNull(capturedConfig.NextScheduledRunAt);
        Assert.True(
            capturedConfig.NextScheduledRunAt >= beforeRun.AddDays(1) &&
            capturedConfig.NextScheduledRunAt <= afterRun.AddDays(1));
    }

    [Fact]
    public async Task ExecuteAsync_RecordsMultiplePricingHistoriesPerProperty()
    {
        // Arrange
        var propertyId = Guid.NewGuid();
        var config = new PricingAdapterConfig
        {
            Id = Guid.NewGuid(),
            PropertyId = propertyId,
            IsEnabled = true,
            IncludeSeasonality = false,
            IncludePublicHolidays = false,
            AdaptationFrequency = "daily"
        };

        _configRepositoryMock.Setup(r => r.GetEnabledConfigsAsync())
            .ReturnsAsync(new[] { config });

        _pricingServiceMock.Setup(s => s.CalculatePricingMultiplierAsync(
            It.IsAny<DateTime>(),
            It.IsAny<bool>(),
            It.IsAny<bool>()))
            .ReturnsAsync(1.2m);

        var historyRecords = new List<PricingHistory>();
        _historyRepositoryMock.Setup(r => r.AddAsync(It.IsAny<PricingHistory>()))
            .Callback<PricingHistory>(h => historyRecords.Add(h))
            .ReturnsAsync((PricingHistory h) => h);

        _otaManagerMock.Setup(m => m.SyncAllAsync(propertyId))
            .ReturnsAsync(true);

        // Act
        await _job.ExecuteAsync();

        // Assert - Should have ~90 history records
        Assert.NotEmpty(historyRecords);
        Assert.True(historyRecords.Count >= 88 && historyRecords.Count <= 92);
        Assert.All(historyRecords, h =>
        {
            Assert.Equal(propertyId, h.PropertyId);
            Assert.Equal("Pending", h.SyncStatus);
            Assert.Equal(0.85m, h.AiConfidence);
        });
    }

    [Fact]
    public async Task ExecuteAsync_CalculatesPricingMultiplierWithConfigSettings()
    {
        // Arrange
        var propertyId = Guid.NewGuid();
        var config = new PricingAdapterConfig
        {
            Id = Guid.NewGuid(),
            PropertyId = propertyId,
            IsEnabled = true,
            IncludeSeasonality = true,
            IncludePublicHolidays = true,
            AdaptationFrequency = "daily"
        };

        _configRepositoryMock.Setup(r => r.GetEnabledConfigsAsync())
            .ReturnsAsync(new[] { config });

        var multiplierCalls = new List<(DateTime date, bool seasonality, bool holidays)>();
        _pricingServiceMock.Setup(s => s.CalculatePricingMultiplierAsync(
            It.IsAny<DateTime>(),
            It.IsAny<bool>(),
            It.IsAny<bool>()))
            .Callback<DateTime, bool, bool>((d, s, h) => multiplierCalls.Add((d, s, h)))
            .ReturnsAsync(1.1m);

        _historyRepositoryMock.Setup(r => r.AddAsync(It.IsAny<PricingHistory>()))
            .ReturnsAsync((PricingHistory h) => h);

        _otaManagerMock.Setup(m => m.SyncAllAsync(propertyId))
            .ReturnsAsync(true);

        // Act
        await _job.ExecuteAsync();

        // Assert
        Assert.NotEmpty(multiplierCalls);
        Assert.All(multiplierCalls, call =>
        {
            Assert.True(call.seasonality);
            Assert.True(call.holidays);
        });
    }

    [Fact]
    public async Task ExecuteAsync_PricingHistorySyncStatusIsPending()
    {
        // Arrange
        var propertyId = Guid.NewGuid();
        var config = new PricingAdapterConfig
        {
            Id = Guid.NewGuid(),
            PropertyId = propertyId,
            IsEnabled = true,
            IncludeSeasonality = false,
            IncludePublicHolidays = false,
            AdaptationFrequency = "daily"
        };

        _configRepositoryMock.Setup(r => r.GetEnabledConfigsAsync())
            .ReturnsAsync(new[] { config });

        _pricingServiceMock.Setup(s => s.CalculatePricingMultiplierAsync(
            It.IsAny<DateTime>(),
            It.IsAny<bool>(),
            It.IsAny<bool>()))
            .ReturnsAsync(1.5m);

        var historyRecords = new List<PricingHistory>();
        _historyRepositoryMock.Setup(r => r.AddAsync(It.IsAny<PricingHistory>()))
            .Callback<PricingHistory>(h => historyRecords.Add(h))
            .ReturnsAsync((PricingHistory h) => h);

        _otaManagerMock.Setup(m => m.SyncAllAsync(propertyId))
            .ReturnsAsync(true);

        // Act
        await _job.ExecuteAsync();

        // Assert
        Assert.All(historyRecords, h => Assert.Equal("Pending", h.SyncStatus));
    }
}
