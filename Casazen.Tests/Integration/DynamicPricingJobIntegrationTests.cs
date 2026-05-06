using Casazen.Core.Entities;
using Casazen.Core.Repositories;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Repositories;
using Casazen.Infrastructure.Services;
using Casazen.Web.BackgroundJobs;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Casazen.Tests.Integration;

/// <summary>
/// Integration tests for DynamicPricingJob.
/// Uses in-memory database to test end-to-end job execution with real repositories.
/// </summary>
public class DynamicPricingJobIntegrationTests : IAsyncLifetime
{
    private readonly AppDbContext _dbContext;
    private readonly IPricingAdapterConfigRepository _configRepository;
    private readonly IPricingHistoryRepository _historyRepository;
    private readonly Mock<IPricingAdapterService> _pricingServiceMock;
    private readonly Mock<IOtaManager> _otaManagerMock;
    private readonly Mock<ILogger<DynamicPricingJob>> _loggerMock;
    private readonly DynamicPricingJob _job;

    public DynamicPricingJobIntegrationTests()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        _dbContext = new AppDbContext(options);
        _configRepository = new PricingAdapterConfigRepository(_dbContext);
        _historyRepository = new PricingHistoryRepository(_dbContext);

        _pricingServiceMock = new Mock<IPricingAdapterService>();
        _otaManagerMock = new Mock<IOtaManager>();
        _loggerMock = new Mock<ILogger<DynamicPricingJob>>();

        _job = new DynamicPricingJob(
            _configRepository,
            _historyRepository,
            _pricingServiceMock.Object,
            _otaManagerMock.Object,
            _loggerMock.Object);
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        await _dbContext.Database.EnsureDeletedAsync();
        await _dbContext.DisposeAsync();
    }

    private Property CreateTestProperty(Guid propertyId, string name, string cinCode, decimal nightlyRate = 100m)
    {
        return new Property
        {
            Id = propertyId,
            OwnerId = "owner@example.com",
            Name = name,
            Description = "Integration test property",
            Address = "Via Roma 1",
            City = "Rome",
            PostalCode = "00100",
            Latitude = 41.9028m,
            Longitude = 12.4964m,
            Bedrooms = 2,
            Bathrooms = 1,
            MaxGuests = 4,
            NightlyRate = nightlyRate,
            CinCode = cinCode,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
    }

    [Fact]
    public async Task ExecuteAsync_WithEnabledConfig_CreatesHistoryRecords()
    {
        // Arrange
        var propertyId = Guid.NewGuid();
        var property = CreateTestProperty(propertyId, "Test Property", "IT-ABC123-DEF456", 100m);

        var config = new PricingAdapterConfig
        {
            Id = Guid.NewGuid(),
            PropertyId = propertyId,
            IsEnabled = true,
            IncludeSeasonality = true,
            IncludePublicHolidays = false,
            AdaptationFrequency = "daily",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        _dbContext.Properties.Add(property);
        _dbContext.PricingAdapterConfigs.Add(config);
        await _dbContext.SaveChangesAsync();

        _pricingServiceMock.Setup(s => s.CalculatePricingMultiplierAsync(
            It.IsAny<DateTime>(),
            It.IsAny<bool>(),
            It.IsAny<bool>()))
            .ReturnsAsync(1.2m);

        _otaManagerMock.Setup(m => m.SyncAllAsync(propertyId))
            .ReturnsAsync(true);

        // Act
        await _job.ExecuteAsync();

        // Assert
        var historyRecords = await _dbContext.PricingHistories
            .Where(h => h.PropertyId == propertyId)
            .ToListAsync();

        Assert.NotEmpty(historyRecords);
        Assert.True(historyRecords.Count >= 88 && historyRecords.Count <= 92);
        Assert.All(historyRecords, h =>
        {
            Assert.Equal(propertyId, h.PropertyId);
            Assert.Equal(120m, h.NewPrice); // 100 * 1.2
            Assert.Equal(100m, h.PreviousPrice);
            Assert.Equal("Pending", h.SyncStatus);
        });
    }

    [Fact]
    public async Task ExecuteAsync_UpdatesConfigTimestamps()
    {
        // Arrange
        var propertyId = Guid.NewGuid();
        var property = CreateTestProperty(propertyId, "Test Property 2", "IT-XYZ789-GHI012", 150m);

        var config = new PricingAdapterConfig
        {
            Id = Guid.NewGuid(),
            PropertyId = propertyId,
            IsEnabled = true,
            IncludeSeasonality = false,
            IncludePublicHolidays = true,
            AdaptationFrequency = "daily",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        _dbContext.Properties.Add(property);
        _dbContext.PricingAdapterConfigs.Add(config);
        await _dbContext.SaveChangesAsync();

        _pricingServiceMock.Setup(s => s.CalculatePricingMultiplierAsync(
            It.IsAny<DateTime>(),
            It.IsAny<bool>(),
            It.IsAny<bool>()))
            .ReturnsAsync(1.0m);

        _otaManagerMock.Setup(m => m.SyncAllAsync(propertyId))
            .ReturnsAsync(true);

        var beforeRun = DateTime.UtcNow;

        // Act
        await _job.ExecuteAsync();

        var afterRun = DateTime.UtcNow;

        // Assert
        var updatedConfig = await _dbContext.PricingAdapterConfigs
            .FirstOrDefaultAsync(c => c.PropertyId == propertyId);

        Assert.NotNull(updatedConfig);
        Assert.NotNull(updatedConfig.LastAdaptedAt);
        Assert.NotNull(updatedConfig.NextScheduledRunAt);
        Assert.True(updatedConfig.LastAdaptedAt >= beforeRun && updatedConfig.LastAdaptedAt <= afterRun);
        Assert.True(
            updatedConfig.NextScheduledRunAt >= beforeRun.AddDays(1) &&
            updatedConfig.NextScheduledRunAt <= afterRun.AddDays(1));
    }

    [Fact]
    public async Task ExecuteAsync_WithDisabledConfig_SkipsProcessing()
    {
        // Arrange
        var propertyId = Guid.NewGuid();
        var property = CreateTestProperty(propertyId, "Disabled Property", "IT-DIS123-ABLE456", 200m);

        var config = new PricingAdapterConfig
        {
            Id = Guid.NewGuid(),
            PropertyId = propertyId,
            IsEnabled = false, // Disabled
            IncludeSeasonality = true,
            IncludePublicHolidays = false,
            AdaptationFrequency = "daily",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        _dbContext.Properties.Add(property);
        _dbContext.PricingAdapterConfigs.Add(config);
        await _dbContext.SaveChangesAsync();

        // Act
        await _job.ExecuteAsync();

        // Assert
        var historyRecords = await _dbContext.PricingHistories
            .Where(h => h.PropertyId == propertyId)
            .ToListAsync();

        Assert.Empty(historyRecords);
        _pricingServiceMock.Verify(s => s.CalculatePricingMultiplierAsync(
            It.IsAny<DateTime>(),
            It.IsAny<bool>(),
            It.IsAny<bool>()), Times.Never);
    }

    [Fact]
    public async Task ExecuteAsync_MultipleProperties_AllProcessedIndependently()
    {
        // Arrange
        var propertyId1 = Guid.NewGuid();
        var propertyId2 = Guid.NewGuid();

        var property1 = CreateTestProperty(propertyId1, "Property 1", "IT-001-001", 100m);
        var property2 = CreateTestProperty(propertyId2, "Property 2", "IT-002-002", 200m);

        var config1 = new PricingAdapterConfig
        {
            Id = Guid.NewGuid(),
            PropertyId = propertyId1,
            IsEnabled = true,
            IncludeSeasonality = true,
            IncludePublicHolidays = false,
            AdaptationFrequency = "daily",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        var config2 = new PricingAdapterConfig
        {
            Id = Guid.NewGuid(),
            PropertyId = propertyId2,
            IsEnabled = true,
            IncludeSeasonality = false,
            IncludePublicHolidays = true,
            AdaptationFrequency = "daily",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        _dbContext.Properties.AddRange(property1, property2);
        _dbContext.PricingAdapterConfigs.AddRange(config1, config2);
        await _dbContext.SaveChangesAsync();

        _pricingServiceMock.Setup(s => s.CalculatePricingMultiplierAsync(
            It.IsAny<DateTime>(),
            It.IsAny<bool>(),
            It.IsAny<bool>()))
            .ReturnsAsync(1.1m);

        _otaManagerMock.Setup(m => m.SyncAllAsync(It.IsAny<Guid>()))
            .ReturnsAsync(true);

        // Act
        await _job.ExecuteAsync();

        // Assert
        var history1 = await _dbContext.PricingHistories
            .Where(h => h.PropertyId == propertyId1)
            .ToListAsync();

        var history2 = await _dbContext.PricingHistories
            .Where(h => h.PropertyId == propertyId2)
            .ToListAsync();

        Assert.NotEmpty(history1);
        Assert.NotEmpty(history2);
        Assert.True(history1.Count >= 88 && history1.Count <= 92);
        Assert.True(history2.Count >= 88 && history2.Count <= 92);
    }

    [Fact]
    public async Task ExecuteAsync_OtaSyncFailureDoesNotPreventsHistoryRecording()
    {
        // Arrange
        var propertyId = Guid.NewGuid();
        var property = CreateTestProperty(propertyId, "OTA Sync Fail Test", "IT-OTA-FAIL", 100m);

        var config = new PricingAdapterConfig
        {
            Id = Guid.NewGuid(),
            PropertyId = propertyId,
            IsEnabled = true,
            IncludeSeasonality = true,
            IncludePublicHolidays = false,
            AdaptationFrequency = "daily",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        _dbContext.Properties.Add(property);
        _dbContext.PricingAdapterConfigs.Add(config);
        await _dbContext.SaveChangesAsync();

        _pricingServiceMock.Setup(s => s.CalculatePricingMultiplierAsync(
            It.IsAny<DateTime>(),
            It.IsAny<bool>(),
            It.IsAny<bool>()))
            .ReturnsAsync(1.3m);

        _otaManagerMock.Setup(m => m.SyncAllAsync(propertyId))
            .ThrowsAsync(new Exception("OTA sync failed"));

        // Act
        await _job.ExecuteAsync(); // Should not throw

        // Assert - History should still be recorded
        var historyRecords = await _dbContext.PricingHistories
            .Where(h => h.PropertyId == propertyId)
            .ToListAsync();

        Assert.NotEmpty(historyRecords);
        Assert.True(historyRecords.Count >= 88 && historyRecords.Count <= 92);
    }
}
