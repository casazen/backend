using Casazen.Core.Entities;
using Casazen.Core.Repositories;
using Casazen.Infrastructure.OTA;
using Casazen.Infrastructure.Services;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Services;

public class OtaManagerBatchPricingTests
{
    private readonly Mock<IPropertyRepository> _mockPropertyRepository;
    private readonly Mock<IPricingHistoryRepository> _mockPricingHistoryRepository;
    private readonly Mock<IChannelFactory> _mockChannelFactory;
    private readonly Mock<ILogger<OtaManager>> _mockLogger;
    private readonly OtaManager _otaManager;

    private const string TestPropertyId = "property_123";
    private const string AirbnbPlatform = "Airbnb";
    private const string BookingPlatform = "BookingCom";

    public OtaManagerBatchPricingTests()
    {
        _mockPropertyRepository = new Mock<IPropertyRepository>();
        _mockPricingHistoryRepository = new Mock<IPricingHistoryRepository>();
        _mockChannelFactory = new Mock<IChannelFactory>();
        _mockLogger = new Mock<ILogger<OtaManager>>();

        _otaManager = new OtaManager(
            _mockPropertyRepository.Object,
            _mockPricingHistoryRepository.Object,
            _mockChannelFactory.Object,
            _mockLogger.Object
        );
    }

    [Fact]
    public async Task UpdatePricingBatchAsync_WithValidProperty_UpdatesAllActiveAdapters()
    {
        var propertyId = Guid.NewGuid();
        var pricesByDate = new Dictionary<DateOnly, decimal>
        {
            { new DateOnly(2026, 5, 1), 150m },
            { new DateOnly(2026, 5, 2), 155m }
        };

        var property = new Property
        {
            Id = propertyId,
            NightlyRate = 140m,
            OtaIntegrations = new List<OtaIntegration>
            {
                new() { Platform = AirbnbPlatform, ExternalPropertyId = "airbnb_123", IsActive = true, SyncEnabled = true },
                new() { Platform = BookingPlatform, ExternalPropertyId = "booking_123", IsActive = true, SyncEnabled = true }
            }
        };

        _mockPropertyRepository.Setup(x => x.GetByIdAsync(propertyId)).ReturnsAsync(property);

        var mockAirbnbAdapter = new Mock<IChannelAdapter>();
        var mockBookingAdapter = new Mock<IChannelAdapter>();

        mockAirbnbAdapter.Setup(x => x.UpdatePricingBatchAsync(
            It.IsAny<string>(),
            It.IsAny<Dictionary<DateOnly, decimal>>()))
            .ReturnsAsync(new Dictionary<DateOnly, bool>
            {
                { new DateOnly(2026, 5, 1), true },
                { new DateOnly(2026, 5, 2), true }
            });

        mockBookingAdapter.Setup(x => x.UpdatePricingBatchAsync(
            It.IsAny<string>(),
            It.IsAny<Dictionary<DateOnly, decimal>>()))
            .ReturnsAsync(new Dictionary<DateOnly, bool>
            {
                { new DateOnly(2026, 5, 1), true },
                { new DateOnly(2026, 5, 2), true }
            });

        _mockChannelFactory.Setup(x => x.GetAdapter(AirbnbPlatform)).Returns(mockAirbnbAdapter.Object);
        _mockChannelFactory.Setup(x => x.GetAdapter(BookingPlatform)).Returns(mockBookingAdapter.Object);

        _mockPricingHistoryRepository.Setup(x => x.AddAsync(It.IsAny<PricingHistory>()))
            .ReturnsAsync((PricingHistory ph) => ph);

        var result = await _otaManager.UpdatePricingBatchAsync(propertyId, pricesByDate);

        Assert.True(result, "Should return success when all adapters succeed");
        mockAirbnbAdapter.Verify(x => x.UpdatePricingBatchAsync("airbnb_123", pricesByDate), Times.Once);
        mockBookingAdapter.Verify(x => x.UpdatePricingBatchAsync("booking_123", pricesByDate), Times.Once);
        _mockPricingHistoryRepository.Verify(x => x.AddAsync(It.IsAny<PricingHistory>()), Times.Once);
    }

    [Fact]
    public async Task UpdatePricingBatchAsync_WithPartialFailure_ReturnsFailureAndLogsFailedDates()
    {
        var propertyId = Guid.NewGuid();
        var pricesByDate = new Dictionary<DateOnly, decimal>
        {
            { new DateOnly(2026, 5, 1), 150m },
            { new DateOnly(2026, 5, 2), 155m }
        };

        var property = new Property
        {
            Id = propertyId,
            NightlyRate = 140m,
            OtaIntegrations = new List<OtaIntegration>
            {
                new() { Platform = AirbnbPlatform, ExternalPropertyId = "airbnb_123", IsActive = true, SyncEnabled = true },
                new() { Platform = BookingPlatform, ExternalPropertyId = "booking_123", IsActive = true, SyncEnabled = true }
            }
        };

        _mockPropertyRepository.Setup(x => x.GetByIdAsync(propertyId)).ReturnsAsync(property);

        var mockAirbnbAdapter = new Mock<IChannelAdapter>();
        var mockBookingAdapter = new Mock<IChannelAdapter>();

        mockAirbnbAdapter.Setup(x => x.UpdatePricingBatchAsync(
            It.IsAny<string>(),
            It.IsAny<Dictionary<DateOnly, decimal>>()))
            .ReturnsAsync(new Dictionary<DateOnly, bool>
            {
                { new DateOnly(2026, 5, 1), true },
                { new DateOnly(2026, 5, 2), true }
            });

        mockBookingAdapter.Setup(x => x.UpdatePricingBatchAsync(
            It.IsAny<string>(),
            It.IsAny<Dictionary<DateOnly, decimal>>()))
            .ReturnsAsync(new Dictionary<DateOnly, bool>
            {
                { new DateOnly(2026, 5, 1), false },
                { new DateOnly(2026, 5, 2), true }
            });

        _mockChannelFactory.Setup(x => x.GetAdapter(AirbnbPlatform)).Returns(mockAirbnbAdapter.Object);
        _mockChannelFactory.Setup(x => x.GetAdapter(BookingPlatform)).Returns(mockBookingAdapter.Object);

        _mockPricingHistoryRepository.Setup(x => x.AddAsync(It.IsAny<PricingHistory>()))
            .ReturnsAsync((PricingHistory ph) => ph);

        var result = await _otaManager.UpdatePricingBatchAsync(propertyId, pricesByDate);

        Assert.False(result, "Should return failure when any adapter fails");
        _mockPricingHistoryRepository.Verify(x => x.AddAsync(It.Is<PricingHistory>(
            ph => ph.SyncStatus == "failed"
        )), Times.Once);
    }

    [Fact]
    public async Task UpdatePricingBatchAsync_WithNoActiveIntegrations_ReturnsFalse()
    {
        var propertyId = Guid.NewGuid();
        var pricesByDate = new Dictionary<DateOnly, decimal>
        {
            { new DateOnly(2026, 5, 1), 150m }
        };

        var property = new Property
        {
            Id = propertyId,
            OtaIntegrations = new List<OtaIntegration>()
        };

        _mockPropertyRepository.Setup(x => x.GetByIdAsync(propertyId)).ReturnsAsync(property);

        var result = await _otaManager.UpdatePricingBatchAsync(propertyId, pricesByDate);

        Assert.False(result, "Should return false when no active integrations");
        _mockPricingHistoryRepository.Verify(x => x.AddAsync(It.IsAny<PricingHistory>()), Times.Never);
    }

    [Fact]
    public async Task UpdatePricingBatchAsync_WithNullProperty_ReturnsFalse()
    {
        var propertyId = Guid.NewGuid();
        var pricesByDate = new Dictionary<DateOnly, decimal>
        {
            { new DateOnly(2026, 5, 1), 150m }
        };

        _mockPropertyRepository.Setup(x => x.GetByIdAsync(propertyId)).ReturnsAsync((Property?)null);

        var result = await _otaManager.UpdatePricingBatchAsync(propertyId, pricesByDate);

        Assert.False(result, "Should return false when property not found");
    }

    [Fact]
    public async Task UpdatePricingBatchAsync_WithEmptyPriceData_ReturnsFalse()
    {
        var propertyId = Guid.NewGuid();
        var pricesByDate = new Dictionary<DateOnly, decimal>();

        var property = new Property
        {
            Id = propertyId,
            OtaIntegrations = new List<OtaIntegration>
            {
                new() { Platform = AirbnbPlatform, ExternalPropertyId = "airbnb_123", IsActive = true, SyncEnabled = true }
            }
        };

        _mockPropertyRepository.Setup(x => x.GetByIdAsync(propertyId)).ReturnsAsync(property);

        var result = await _otaManager.UpdatePricingBatchAsync(propertyId, pricesByDate);

        Assert.False(result, "Should return false with empty price data");
    }

    [Fact]
    public async Task UpdatePricingBatchAsync_WritesPricingHistoryWithCorrectSyncStatus()
    {
        var propertyId = Guid.NewGuid();
        var pricesByDate = new Dictionary<DateOnly, decimal>
        {
            { new DateOnly(2026, 5, 1), 150m }
        };

        var property = new Property
        {
            Id = propertyId,
            NightlyRate = 140m,
            OtaIntegrations = new List<OtaIntegration>
            {
                new() { Platform = AirbnbPlatform, ExternalPropertyId = "airbnb_123", IsActive = true, SyncEnabled = true }
            }
        };

        _mockPropertyRepository.Setup(x => x.GetByIdAsync(propertyId)).ReturnsAsync(property);

        var mockAdapter = new Mock<IChannelAdapter>();
        mockAdapter.Setup(x => x.UpdatePricingBatchAsync(
            It.IsAny<string>(),
            It.IsAny<Dictionary<DateOnly, decimal>>()))
            .ReturnsAsync(new Dictionary<DateOnly, bool> { { new DateOnly(2026, 5, 1), true } });

        _mockChannelFactory.Setup(x => x.GetAdapter(AirbnbPlatform)).Returns(mockAdapter.Object);

        PricingHistory? capturedHistory = null;
        _mockPricingHistoryRepository.Setup(x => x.AddAsync(It.IsAny<PricingHistory>()))
            .Callback<PricingHistory>(ph => capturedHistory = ph)
            .ReturnsAsync((PricingHistory ph) => ph);

        var result = await _otaManager.UpdatePricingBatchAsync(propertyId, pricesByDate);

        Assert.True(result);
        Assert.NotNull(capturedHistory);
        Assert.Equal("synced", capturedHistory.SyncStatus);
        Assert.Equal(propertyId, capturedHistory.PropertyId);
        Assert.Equal(140m, capturedHistory.PreviousPrice);
        Assert.Equal(150m, capturedHistory.NewPrice);
    }

    [Fact]
    public async Task UpdatePricingBatchAsync_OnException_LogsErrorAndReturnsFalse()
    {
        var propertyId = Guid.NewGuid();
        var pricesByDate = new Dictionary<DateOnly, decimal>
        {
            { new DateOnly(2026, 5, 1), 150m }
        };

        _mockPropertyRepository.Setup(x => x.GetByIdAsync(propertyId))
            .ThrowsAsync(new InvalidOperationException("Database error"));

        var result = await _otaManager.UpdatePricingBatchAsync(propertyId, pricesByDate);

        Assert.False(result, "Should return false on exception");
        _mockLogger.Verify(
            x => x.Log(
                LogLevel.Error,
                It.IsAny<EventId>(),
                It.IsAny<It.IsAnyType>(),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.AtLeastOnce
        );
    }

    [Fact]
    public async Task UpdatePricingBatchAsync_OnAdapterException_ContinuesWithOtherAdapters()
    {
        var propertyId = Guid.NewGuid();
        var pricesByDate = new Dictionary<DateOnly, decimal>
        {
            { new DateOnly(2026, 5, 1), 150m }
        };

        var property = new Property
        {
            Id = propertyId,
            NightlyRate = 140m,
            OtaIntegrations = new List<OtaIntegration>
            {
                new() { Platform = AirbnbPlatform, ExternalPropertyId = "airbnb_123", IsActive = true, SyncEnabled = true },
                new() { Platform = BookingPlatform, ExternalPropertyId = "booking_123", IsActive = true, SyncEnabled = true }
            }
        };

        _mockPropertyRepository.Setup(x => x.GetByIdAsync(propertyId)).ReturnsAsync(property);

        var mockAirbnbAdapter = new Mock<IChannelAdapter>();
        var mockBookingAdapter = new Mock<IChannelAdapter>();

        mockAirbnbAdapter.Setup(x => x.UpdatePricingBatchAsync(It.IsAny<string>(), It.IsAny<Dictionary<DateOnly, decimal>>()))
            .ThrowsAsync(new HttpRequestException("Network error"));

        mockBookingAdapter.Setup(x => x.UpdatePricingBatchAsync(It.IsAny<string>(), It.IsAny<Dictionary<DateOnly, decimal>>()))
            .ReturnsAsync(new Dictionary<DateOnly, bool> { { new DateOnly(2026, 5, 1), true } });

        _mockChannelFactory.Setup(x => x.GetAdapter(AirbnbPlatform)).Returns(mockAirbnbAdapter.Object);
        _mockChannelFactory.Setup(x => x.GetAdapter(BookingPlatform)).Returns(mockBookingAdapter.Object);

        _mockPricingHistoryRepository.Setup(x => x.AddAsync(It.IsAny<PricingHistory>()))
            .ReturnsAsync((PricingHistory ph) => ph);

        var result = await _otaManager.UpdatePricingBatchAsync(propertyId, pricesByDate);

        Assert.False(result, "Should return false due to Airbnb exception");
        mockBookingAdapter.Verify(x => x.UpdatePricingBatchAsync(It.IsAny<string>(), It.IsAny<Dictionary<DateOnly, decimal>>()), Times.Once, "Should still call Booking adapter");
        _mockPricingHistoryRepository.Verify(x => x.AddAsync(It.IsAny<PricingHistory>()), Times.Once);
    }
}
