using Casazen.Core.Entities;
using Casazen.Core.Enums;
using Casazen.Core.Repositories;
using Casazen.Infrastructure.Services;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Services;

public class PropertyServiceGetDetailTests
{
    private readonly Mock<IPropertyRepository> _mockRepository;
    private readonly PropertyService _service;

    public PropertyServiceGetDetailTests()
    {
        _mockRepository = new Mock<IPropertyRepository>();
        _service = new PropertyService(_mockRepository.Object, new Mock<ILogger<PropertyService>>().Object);
    }

    [Fact]
    public async Task GetPropertyDetailAsync_WithValidId_ReturnsPropertyDetailResponse()
    {
        // Arrange
        var propertyId = Guid.NewGuid();
        var now = DateTime.UtcNow;
        var property = new Property
        {
            Id = propertyId,
            OwnerId = "auth0|owner123",
            Name = "Villa Roma",
            Description = "Beautiful villa",
            Address = "Via Roma 1",
            City = "Rome",
            PostalCode = "00100",
            Bedrooms = 3,
            Bathrooms = 2,
            MaxGuests = 6,
            NightlyRate = 150m,
            CleaningFee = 50m,
            DamageDeposit = 200m,
            CinCode = "IT-12345-0123456789",
            IsActive = true,
            CreatedAt = now.AddDays(-30),
            UpdatedAt = now
        };

        property.PropertyDocuments.Add(new PropertyDocument
        {
            Id = Guid.NewGuid(),
            PropertyId = propertyId,
            FileName = "cin-cert.pdf",
            StorageUrl = "/uploads/properties/cin-cert.pdf",
            DocumentType = DocumentType.CinCertificate,
            UploadedBy = "owner@example.com",
            UploadedAt = now.AddDays(-10)
        });

        property.OtaIntegrations.Add(new OtaIntegration
        {
            Id = Guid.NewGuid(),
            PropertyId = propertyId,
            Platform = "Airbnb",
            ExternalPropertyId = "airbnb-123",
            IsActive = true,
            SyncEnabled = true,
            LastSyncAt = now.AddHours(-1),
            SyncStatus = "Success"
        });

        property.Bookings.Add(new Booking
        {
            Id = Guid.NewGuid(),
            PropertyId = propertyId,
            GuestId = Guid.NewGuid(),
            CheckInDate = now.AddDays(10),
            CheckOutDate = now.AddDays(15),
            Status = BookingStatus.Confirmed
        });

        _mockRepository.Setup(x => x.GetPropertyDetailAsync(propertyId)).ReturnsAsync(property);

        // Act
        var result = await _service.GetPropertyDetailAsync(propertyId);

        // Assert
        Assert.NotNull(result);
        Assert.Equal(propertyId, result.Id);
        Assert.Equal("Villa Roma", result.Name);
        Assert.Equal("Rome", result.City);
        Assert.Single(result.Documents);
        Assert.Single(result.OtaIntegrations);
        Assert.Equal(1, result.BookingsSummary.TotalBookings);
        Assert.Equal(1, result.BookingsSummary.UpcomingBookings);
        _mockRepository.Verify(x => x.GetPropertyDetailAsync(propertyId), Times.Once);
    }

    [Fact]
    public async Task GetPropertyDetailAsync_WithNonExistentId_ThrowsInvalidOperationException()
    {
        // Arrange
        var propertyId = Guid.NewGuid();

        _mockRepository.Setup(x => x.GetPropertyDetailAsync(propertyId)).ReturnsAsync((Property?)null);

        // Act & Assert
        await Assert.ThrowsAsync<InvalidOperationException>(() => _service.GetPropertyDetailAsync(propertyId));
        _mockRepository.Verify(x => x.GetPropertyDetailAsync(propertyId), Times.Once);
    }

    [Fact]
    public async Task GetPropertyDetailAsync_OtaIntegrations_DoNotExposeApiKey()
    {
        // Arrange
        var propertyId = Guid.NewGuid();
        var property = new Property
        {
            Id = propertyId,
            OwnerId = "auth0|owner123",
            Name = "Test Property",
            Address = "Via Test 1",
            City = "Milan",
            IsActive = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        property.OtaIntegrations.Add(new OtaIntegration
        {
            Id = Guid.NewGuid(),
            PropertyId = propertyId,
            Platform = "Booking.com",
            ExternalPropertyId = "booking-456",
            ApiKey = "secret-api-key-12345",
            ApiSecret = "secret-api-secret-67890",
            IsActive = true,
            SyncEnabled = true
        });

        _mockRepository.Setup(x => x.GetPropertyDetailAsync(propertyId)).ReturnsAsync(property);

        // Act
        var result = await _service.GetPropertyDetailAsync(propertyId);

        // Assert
        Assert.NotNull(result);
        Assert.Single(result.OtaIntegrations);

        var otaDto = result.OtaIntegrations[0];
        var otaDtoType = otaDto.GetType();

        Assert.Null(otaDtoType.GetProperty("ApiKey"));
        Assert.Null(otaDtoType.GetProperty("ApiSecret"));
    }

    [Fact]
    public async Task GetPropertyDetailAsync_WithValidCinCode_SetsCinStatusValid()
    {
        // Arrange
        var propertyId = Guid.NewGuid();
        var property = new Property
        {
            Id = propertyId,
            OwnerId = "auth0|owner123",
            Name = "Test Property",
            Address = "Via Test 1",
            City = "Rome",
            CinCode = "IT-12345-0123456789",
            IsActive = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        _mockRepository.Setup(x => x.GetPropertyDetailAsync(propertyId)).ReturnsAsync(property);

        // Act
        var result = await _service.GetPropertyDetailAsync(propertyId);

        // Assert
        Assert.Equal(CinStatus.Valid, result.CinStatus);
    }

    [Fact]
    public async Task GetPropertyDetailAsync_WithNullCinCode_SetsCinStatusMissing()
    {
        // Arrange
        var propertyId = Guid.NewGuid();
        var property = new Property
        {
            Id = propertyId,
            OwnerId = "auth0|owner123",
            Name = "Test Property",
            Address = "Via Test 1",
            City = "Rome",
            CinCode = null,
            IsActive = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        _mockRepository.Setup(x => x.GetPropertyDetailAsync(propertyId)).ReturnsAsync(property);

        // Act
        var result = await _service.GetPropertyDetailAsync(propertyId);

        // Assert
        Assert.Equal(CinStatus.Missing, result.CinStatus);
    }

    [Fact]
    public async Task GetPropertyDetailAsync_WithInvalidCinCode_SetsCinStatusInvalid()
    {
        // Arrange
        var propertyId = Guid.NewGuid();
        var property = new Property
        {
            Id = propertyId,
            OwnerId = "auth0|owner123",
            Name = "Test Property",
            Address = "Via Test 1",
            City = "Rome",
            CinCode = "INVALID",
            IsActive = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        _mockRepository.Setup(x => x.GetPropertyDetailAsync(propertyId)).ReturnsAsync(property);

        // Act
        var result = await _service.GetPropertyDetailAsync(propertyId);

        // Assert
        Assert.Equal(CinStatus.Invalid, result.CinStatus);
    }
}
